import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { createServer as createTcpServer } from 'node:net';
import { exportJWK, generateKeyPair, importJWK, jwtVerify, SignJWT, decodeJwt, decodeProtectedHeader } from 'jose';
import { presentationService } from '../presentation.mjs';
import { signingService, digest, random } from '../crypto.mjs';
import { createIssuer } from '../server.mjs';
import { AuthorizationRequest } from '@sphereon/did-auth-siop';

async function fixture(t) {
  const issuer = await generateKeyPair('ES256', { extractable: true });
  const holder = await generateKeyPair('ES256', { extractable: true });
  const holderJwk = await exportJWK(holder.publicKey);
  let time = Math.floor(Date.now() / 1000);
  const config = {
    origin: 'https://agentcon-issuer.example', privateJwk: await exportJWK(issuer.privateKey),
    adminToken: random(), now: () => time, maxEntries: 10, rateLimit: 1000, rateWindow: 60,
    offerTtl: 600, tokenTtl: 600, nonceTtl: 300,
  };
  const signer = await signingService(config);
  const verifier = presentationService(config, signer.publicJwk);
  const credential = await signer.issue(holderJwk, time);
  async function presentation(challenge, options = {}) {
    const request = decodeJwt(verifier.request(challenge.id));
    const sd = options.credential ?? credential;
    const kb = await new SignJWT({
      aud: request.client_id, nonce: request.nonce, iat: time, sd_hash: digest(sd), ...options.claims,
    }).setProtectedHeader({ alg: 'ES256', typ: 'kb+jwt' }).sign(options.key ?? holder.privateKey);
    return { state: request.state, vp_token: JSON.stringify({ age: [`${sd}${kb}`] }) };
  }
  const server = await createIssuer(config);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }));
  const local = `http://127.0.0.1:${server.address().port}`;
  return { config, signer, verifier, credential, holder, holderJwk, presentation, local,
    advance: seconds => { time += seconds; } };
}

test('OID4VP DCQL request is task-bound, minimal, and verifies issuer plus holder binding', async t => {
  const f = await fixture(t);
  const challenge = f.verifier.create('purchase-task', 'wine-context');
  const request = decodeJwt(f.verifier.request(challenge.id));
  assert.equal(request.client_id, challenge.authorizationRequest.client_id);
  assert.equal(request.response_mode, 'direct_post');
  assert.equal(request.client_id, f.signer.publicJwk.kid.split('#')[0]);
  const signedRequest = f.verifier.request(challenge.id);
  assert.ok(signedRequest.split('.')[2], 'Request must have a nonempty signature');
  assert.deepEqual(decodeProtectedHeader(signedRequest), {
    alg: 'ES256', typ: 'oauth-authz-req+jwt', kid: f.signer.publicJwk.kid,
  });

  const verifiedRequest = await jwtVerify(signedRequest, await importJWK(f.signer.publicJwk, 'ES256'), {
    issuer: request.client_id, audience: 'https://self-issued.me/v2',
  });
  assert.equal(verifiedRequest.payload.exp, challenge.expiresAt);
  assert.deepEqual(request.dcql_query.credentials[0].claims, [{ path: ['over_21'], values: [true] }]);
  assert.equal(f.verifier.status(challenge.id).verified, false);
  const response = await f.presentation(challenge);
  await f.verifier.verify(challenge.id, response);
  assert.deepEqual(f.verifier.status(challenge.id), {
    taskId: 'purchase-task', contextId: 'wine-context', verified: true, expiresAt: challenge.expiresAt,
  });
  await assert.rejects(f.verifier.verify(challenge.id, response), /already submitted/);
});

test('Sphereon 0.8.0 pinned request parser verifies signed DID request and resolves DCQL', async t => {
  const f = await fixture(t);
  const challenge = f.verifier.create('sphereon-task', 'sphereon-context');
  const request = await AuthorizationRequest.fromUriOrJwt(f.verifier.request(challenge.id));
  let checked = false;
  const result = await request.verify({
    verification: {}, correlationId: 'sphereon-compat',
    verifyJwtCallback: async (verifier, jwt) => {
      assert.equal(verifier.method, 'did');
      assert.equal(verifier.didUrl, f.signer.publicJwk.kid);
      await jwtVerify(jwt.raw, await importJWK(f.signer.publicJwk, 'ES256'), {
        algorithms: ['ES256'], issuer: challenge.authorizationRequest.client_id,
        audience: 'https://self-issued.me/v2',
      });
      checked = true;
      return true;
    },
  });
  assert.equal(checked, true);
  assert.equal(result.responseURI, `${f.config.origin}/presentations/${challenge.id}/response`);
  assert.ok(result.dcqlQuery);
});

test('rejects wrong nonce, audience, state, signing key, disclosure hash and missing proof', async t => {
  const f = await fixture(t);
  const wrongKey = await generateKeyPair('ES256');
  for (const options of [
    { claims: { nonce: random() } }, { claims: { aud: 'other-task' } },
    { claims: { sd_hash: random() } }, { claims: { iat: f.config.now() - 301 } },
    { key: wrongKey.privateKey }, { credential: f.credential.split('~')[0] + '~' },
  ]) {
    const challenge = f.verifier.create(random(), 'context');
    await assert.rejects(f.verifier.verify(challenge.id, await f.presentation(challenge, options)));
    assert.equal(f.verifier.status(challenge.id).verified, false);
  }
  const challenge = f.verifier.create('task', 'context');
  const valid = await f.presentation(challenge);
  await assert.rejects(f.verifier.verify(challenge.id, { ...valid, state: random() }));
  await assert.rejects(f.verifier.verify(challenge.id, { ...valid, vp_token: JSON.stringify({ age: [f.credential] }) }));
  const other = f.verifier.create('other-task', 'context');
  await assert.rejects(f.verifier.verify(other.id, valid));
});

test('rejects forged issuer, expired credential, false claim and concurrent replay', async t => {
  const f = await fixture(t);
  const otherKey = await generateKeyPair('ES256', { extractable: true });
  const otherIssuer = await signingService({ ...f.config, privateJwk: await exportJWK(otherKey.privateKey) });
  const forged = await otherIssuer.issue(f.holderJwk, f.config.now());
  const challenge = f.verifier.create('task', 'context');
  await assert.rejects(f.verifier.verify(challenge.id, await f.presentation(challenge, { credential: forged })));
  const expired = await f.signer.issue(f.holderJwk, f.config.now() - 15 * 86400);
  await assert.rejects(f.verifier.verify(challenge.id, await f.presentation(challenge, { credential: expired })));
  const falseClaim = await new SignJWT({
    iss: f.config.origin, iat: f.config.now(), exp: f.config.now() + 600,
    vct: 'urn:agentcon:demo:age-over-21', demo_only: true, over_21: false, cnf: { jwk: f.holderJwk },
  }).setProtectedHeader({ alg: 'ES256', typ: 'dc+sd-jwt', kid: f.signer.publicJwk.kid })
    .sign(await importJWK(f.config.privateJwk, 'ES256'));
  await assert.rejects(f.verifier.verify(challenge.id, await f.presentation(challenge, { credential: falseClaim + '~' })));
  const response = await f.presentation(challenge);
  const results = await Promise.allSettled([f.verifier.verify(challenge.id, response), f.verifier.verify(challenge.id, response)]);
  assert.equal(results.filter(r => r.status === 'fulfilled').length, 1);
  f.advance(601);
  assert.throws(() => f.verifier.status(challenge.id), /expired/);
});

test('HTTP handoff exposes QR and request but status and session creation require supplier authentication', async t => {
  const f = await fixture(t);
  const create = token => fetch(`${f.local}/admin/presentations`, {
    method: 'POST', headers: { 'content-type': 'application/json', ...(token && { authorization: `Bearer ${token}` }) },
    body: JSON.stringify({ taskId: 'task', contextId: 'context' }),
  });
  assert.equal((await create()).status, 401);
  const created = await create(f.config.adminToken);
  assert.equal(created.status, 201);
  const challenge = await created.json();
  const path = new URL(challenge.presentationUrl).pathname;
  const page = await fetch(f.local + path);
  assert.equal(page.headers.get('cache-control'), 'no-store');
  assert.match(await page.text(), /data:image\/png;base64/);
  const request = await fetch(f.local + path + '/request');
  assert.equal(request.headers.get('content-type'), 'application/oauth-authz-req+jwt');
  const object = decodeJwt(await request.text());
  assert.equal(object.client_id, challenge.authorizationRequest.client_id);
  assert.equal((await fetch(`${f.local}/admin/presentations/${challenge.id}`)).status, 401);
  const credential = await f.signer.issue(f.holderJwk, f.config.now());
  const kb = await new SignJWT({ aud: object.client_id, nonce: object.nonce, iat: f.config.now(), sd_hash: digest(credential) })
    .setProtectedHeader({ alg: 'ES256', typ: 'kb+jwt' }).sign(f.holder.privateKey);
  const result = await fetch(f.local + path + '/response', {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ state: object.state, vp_token: JSON.stringify({ age: [credential + kb] }) }),
  });
  assert.equal(result.status, 200, await result.text());
  const status = await fetch(`${f.local}/admin/presentations/${challenge.id}`, {
    headers: { authorization: `Bearer ${f.config.adminToken}` },
  });
  assert.equal((await status.json()).verified, true);
});

test('real Rust supplier requires issuer verifier approval and resumes the original purchase', {
  skip: !process.env.AGENTCON_SUPPLIER_EXE,
}, async t => {
  const f = await fixture(t);
  f.config.origin = f.local;
  const reservation = createTcpServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.env.AGENTCON_SUPPLIER_EXE, [], {
    env: { ...process.env, SUPPLIER_PORT: String(port), SUPPLIER_VERIFIER_URL: f.local,
      SUPPLIER_VERIFIER_TOKEN: f.config.adminToken, OTEL_EXPORTER_OTLP_ENDPOINT: '' },
    stdio: 'ignore',
  });
  t.after(() => child.kill());
  const origin = `http://127.0.0.1:${port}`;
  let ready = false;
  for (let i = 0; i < 100 && child.exitCode === null; i++) {
    try { ready = (await fetch(origin + '/health')).ok; } catch { /* Startup is not yet listening. */ }
    if (ready) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.equal(ready, true, 'Rust supplier did not start');
  const context = 'wallet-http-context';
  async function send(data, taskId) {
    const response = await fetch(origin + '/a2a', {
      method: 'POST', headers: { 'content-type': 'application/json', 'A2A-Version': '1.0',
        'A2A-Extensions': 'https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/v1' },
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'SendMessage', params: {
        message: { messageId: random(), role: 'ROLE_USER', contextId: context, ...(taskId && { taskId }),
          parts: [{ data, mediaType: `application/vnd.agentcon.demo.wine-${data.kind === 'rfq' ? 'rfq' : 'purchase-order'}.v1+json` }] },
        configuration: { returnImmediately: false, acceptedOutputModes: ['text/plain',
          'application/vnd.agentcon.demo.wine-quote.v1+json', 'application/vnd.agentcon.demo.wine-order-receipt.v1+json'] },
      } }),
    });
    const value = await response.json();
    assert.equal(value.error, undefined);
    return value.result.task;
  }
  const quoteTask = await send({ kind: 'rfq', schemaVersion: '1', demoOnly: true,
    wine: 'Barolo', vintage: 2019, cases: 1, deliveryCity: 'Toronto', deliveryCountry: 'CA' });
  assert.equal(quoteTask.status.state, 'TASK_STATE_COMPLETED');
  const quote = quoteTask.artifacts[0].parts[0].data;
  const order = { kind: 'purchase-order', schemaVersion: '1', demoOnly: true, quoteId: quote.quoteId, purchaseOrderId: 'wallet-po' };
  const pending = await send(order);
  assert.equal(pending.status.state, 'TASK_STATE_AUTH_REQUIRED');
  assert.ok(!pending.artifacts?.length);
  const waiting = await send(order, pending.id);
  assert.equal(waiting.status.state, 'TASK_STATE_AUTH_REQUIRED');
  const metadata = pending.status.message.metadata;
  const requestUri = metadata['https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/v1'].authorizationRequest.request_uri;
  const request = decodeJwt(await (await fetch(requestUri)).text());
  const credential = await f.signer.issue(f.holderJwk, f.config.now());
  const kb = await new SignJWT({ aud: request.client_id, nonce: request.nonce, iat: f.config.now(), sd_hash: digest(credential) })
    .setProtectedHeader({ alg: 'ES256', typ: 'kb+jwt' }).sign(f.holder.privateKey);
  const verified = await fetch(request.response_uri, { method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ state: request.state, vp_token: JSON.stringify({ age: [credential + kb] }) }),
  });
  assert.equal(verified.status, 200);
  const complete = await send({ ...order, quoteId: 'tampered', purchaseOrderId: 'tampered' }, pending.id);
  assert.equal(complete.id, pending.id);
  assert.equal(complete.status.state, 'TASK_STATE_COMPLETED');
  const receipt = complete.artifacts[0].parts[0].data;
  assert.equal(receipt.purchaseOrderId, 'wallet-po');
  assert.equal(receipt.quoteId, quote.quoteId);
  assert.equal(receipt.ageVerification, 'verified-demo-over-21');
  assert.equal((await (await fetch(origin + '/health')).json()).inferenceAttempts, 0);
});
