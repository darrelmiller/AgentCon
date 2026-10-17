import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { createServer as createTcpServer } from 'node:net';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { exportJWK, generateKeyPair, importJWK, jwtVerify, SignJWT, decodeJwt, decodeProtectedHeader } from 'jose';
import { presentationService } from '../presentation.mjs';
import { signingService, validateProof, credentialType, LEGACY_VCT, digest, random } from '../crypto.mjs';
import { createIssuer } from '../server.mjs';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';
import { createHash, createPrivateKey, sign } from 'node:crypto';
import * as EcdsaMultikey from '@digitalbazaar/ecdsa-multikey';

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
  const verifier = await presentationService(config, signer.publicJwk);
  const credential = await signer.issue(holderJwk, time);
  async function presentation(challenge, options = {}) {
    const request = options.request ?? decodeJwt(verifier.request(challenge.id));
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
  const did = `did:jwk:${Buffer.from(JSON.stringify({
    kty: f.signer.publicJwk.kty, crv: f.signer.publicJwk.crv,
    x: f.signer.publicJwk.x, y: f.signer.publicJwk.y,
  })).toString('base64url')}`;
  assert.equal(request.client_id, `decentralized_identifier:${did}`);
  const signedRequest = f.verifier.request(challenge.id);
  assert.ok(signedRequest.split('.')[2], 'Request must have a nonempty signature');
  assert.deepEqual(decodeProtectedHeader(signedRequest), {
    alg: 'ES256', typ: 'oauth-authz-req+jwt', kid: `${did}#0`,
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

test('Light SSI proof shape issues a credential that can be presented to the supplier verifier', async t => {
  const f = await fixture(t);
  const nonce = random();
  const proof = await new SignJWT({
    aud: f.config.origin, iat: f.config.now(), nonce, iss: 'wallet-client-id',
  }).setProtectedHeader({
    alg: 'ES256', typ: 'openid4vci-proof+jwt',
    jwk: f.holderJwk, kid: 'did:key:wallet-key#signing-key',
  }).sign(f.holder.privateKey);
  const { jwk } = await validateProof(proof, f.config.origin, f.config.now());
  const credential = await f.signer.issue(jwk, f.config.now());
  assert.equal(decodeProtectedHeader(credential.split('~')[0]).kid, f.signer.legacyPublicJwk.kid);

  const challenge = f.verifier.create('light-ssi-presentation', 'wallet-context');
  await f.verifier.verify(challenge.id, await f.presentation(challenge, { credential }));
  assert.equal(f.verifier.status(challenge.id).verified, true);
});

test('Light SSI DID key and Sphereon DID JWK request profiles verify their bound presentations', async t => {
  const f = await fixture(t);
  const challenge = f.verifier.create('wallet-profile-task', 'wallet-profile-context');
  const didKeyRequest = f.verifier.lightSsiRequest(challenge.id);
  const didKeyHeader = decodeProtectedHeader(didKeyRequest);
  const fingerprint = didKeyHeader.kid.split('#')[0].slice('did:key:'.length);
  assert.equal(didKeyHeader.kid, `did:key:${fingerprint}#${fingerprint}`);
  const didKey = await EcdsaMultikey.from({ publicKeyMultibase: fingerprint });
  const publicJwk = await EcdsaMultikey.toJwk({ keyPair: didKey });
  assert.equal(publicJwk.x, f.signer.publicJwk.x);
  assert.equal(publicJwk.y, f.signer.publicJwk.y);
  await jwtVerify(didKeyRequest, await importJWK(publicJwk, 'ES256'), {
    algorithms: ['ES256'], typ: 'oauth-authz-req+jwt',
  });
  const request = decodeJwt(didKeyRequest);
  assert.equal(request.client_id, `decentralized_identifier:did:key:${fingerprint}`);
  assert.equal(request.iss, request.client_id);
  assert.equal(decodeJwt(f.verifier.request(challenge.id)).client_id,
    challenge.authorizationRequest.client_id);

  await f.verifier.verify(challenge.id, await f.presentation(challenge, { request }));
  assert.equal(f.verifier.status(challenge.id).verified, true);
});

test('final OID4VP DID prefix, HTTPS type and signed request resolve without vendor SDKs', async t => {
  const f = await fixture(t);
  const challenge = f.verifier.create('standard-task', 'standard-context');
  const jwt = f.verifier.request(challenge.id);
  const header = decodeProtectedHeader(jwt);
  const did = header.kid.split('#')[0];
  const publicJwk = JSON.parse(Buffer.from(did.slice('did:jwk:'.length), 'base64url'));
  const result = await jwtVerify(jwt, await importJWK(publicJwk, 'ES256'), {
    algorithms: ['ES256'], typ: 'oauth-authz-req+jwt',
    issuer: challenge.authorizationRequest.client_id, audience: 'https://self-issued.me/v2',
  });
  assert.equal(result.payload.client_id, `decentralized_identifier:${did}`);
  assert.equal(result.payload.response_uri, `${f.config.origin}/presentations/${challenge.id}/response`);
  assert.deepEqual(result.payload.dcql_query.credentials[0].meta.vct_values,
    [credentialType(f.config.origin), LEGACY_VCT]);
});

test('existing URN credentials with the original key ID remain verifiable', async t => {
  const f = await fixture(t);
  const sd = new SDJwtVcInstance({
    signAlg: 'ES256', hashAlg: 'sha-256',
    hasher: data => createHash('sha256').update(data).digest(),
    saltGenerator: () => random(),
    signer: data => sign('sha256', Buffer.from(data), {
      key: createPrivateKey({ key: f.config.privateJwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363',
    }).toString('base64url'),
  });
  const credential = await sd.issue({
    iss: f.config.origin, iat: f.config.now(), exp: f.config.now() + 600,
    vct: LEGACY_VCT, demo_only: true, cnf: { jwk: f.holderJwk }, over_21: true,
  }, { _sd: ['over_21'] }, { header: { typ: 'dc+sd-jwt', kid: f.signer.legacyPublicJwk.kid } });
  const challenge = f.verifier.create('legacy-task', 'context');
  await f.verifier.verify(challenge.id, await f.presentation(challenge, { credential }));
  assert.equal(f.verifier.status(challenge.id).verified, true);
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

test('issuer signature alone does not bypass allowed key IDs or credential types', async t => {
  const f = await fixture(t);
  for (const options of [
    { kid: undefined }, { kid: 'unrecognized-key' },
    { kid: f.signer.publicJwk.kid, vct: 'https://untrusted.example/type' },
  ]) {
    const [signed, ...disclosures] = f.credential.split('~');
    const jwt = await new SignJWT({ ...decodeJwt(signed), ...(options.vct && { vct: options.vct }) })
      .setProtectedHeader({ alg: 'ES256', typ: 'dc+sd-jwt', ...(options.kid && { kid: options.kid }) })
      .sign(await importJWK(f.config.privateJwk, 'ES256'));
    const credential = [jwt, ...disclosures].join('~');
    const challenge = f.verifier.create('task', 'context');
    await assert.rejects(f.verifier.verify(challenge.id, await f.presentation(challenge, { credential })));
    assert.equal(f.verifier.status(challenge.id).verified, false);
  }
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
  const html = await page.text();
  assert.equal((html.match(/data:image\/png;base64/g) ?? []).length, 2);
  assert.match(html, /Light SSI and DID key wallets/);
  const request = await fetch(f.local + path + '/request');
  assert.equal(request.headers.get('access-control-allow-origin'), '*');
  assert.equal(request.headers.get('content-type'), 'application/oauth-authz-req+jwt');
  const object = decodeJwt(await request.text());
  assert.equal(object.client_id, challenge.authorizationRequest.client_id);
  const lightRequest = await fetch(f.local + path + '/request/light-ssi');
  assert.equal(lightRequest.status, 200);
  assert.equal(lightRequest.headers.get('access-control-allow-origin'), '*');
  assert.match(decodeJwt(await lightRequest.text()).client_id, /^decentralized_identifier:did:key:/);
  assert.equal((await fetch(`${f.local}/admin/presentations/${challenge.id}`)).status, 401);
  const credential = await f.signer.issue(f.holderJwk, f.config.now());
  const kb = await new SignJWT({ aud: object.client_id, nonce: object.nonce, iat: f.config.now(), sd_hash: digest(credential) })
    .setProtectedHeader({ alg: 'ES256', typ: 'kb+jwt' }).sign(f.holder.privateKey);
  const result = await fetch(f.local + path + '/response', {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ state: object.state, vp_token: JSON.stringify({ age: [credential + kb] }) }),
  });
  assert.equal(result.status, 200, await result.text());
  assert.equal(result.headers.get('access-control-allow-origin'), '*');
  for (const [suffix, method] of [['request', 'GET'], ['response', 'POST']]) {
    const preflight = await fetch(`${f.local}${path}/${suffix}`, {
      method: 'OPTIONS', headers: { origin: 'https://wallet.example',
        'access-control-request-method': method, 'access-control-request-headers': 'content-type' },
    });
    assert.equal(preflight.status, 204);
    assert.equal(preflight.headers.get('access-control-allow-origin'), '*');
  }
  const status = await fetch(`${f.local}/admin/presentations/${challenge.id}`, {
    headers: { authorization: `Bearer ${f.config.adminToken}` },
  });
  assert.equal((await status.json()).verified, true);
});

for (const route of ['supplier', 'buyer']) test(`real ${route} requires issuer verifier approval and resumes the original purchase without inference`, {
  skip: !process.env.AGENTCON_SUPPLIER_EXE || route === 'buyer' && !process.env.AGENTCON_BUYER_EXE,
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
      SUPPLIER_VERIFIER_TOKEN: f.config.adminToken, SUPPLIER_STRUCTURED_DATA: 'true',
      SUPPLIER_DEMO_CONTROL_TOKEN: f.config.adminToken, OTEL_EXPORTER_OTLP_ENDPOINT: '' },
    stdio: 'ignore',
  });
  t.after(() => child.kill());
  const supplierOrigin = `http://127.0.0.1:${port}`;
  let origin = supplierOrigin;
  let ready = false;
  for (let i = 0; i < 100 && child.exitCode === null; i++) {
    try { ready = (await fetch(origin + '/health')).ok; } catch { /* Startup is not yet listening. */ }
    if (ready) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.equal(ready, true, 'Rust supplier did not start');
  if (route === 'buyer') {
    const directory = await mkdtemp(join(tmpdir(), 'agentcon-native-wallet-'));
    const catalog = join(directory, 'catalog.json');
    await writeFile(catalog, JSON.stringify({
      specVersion: '1.0', host: { displayName: 'Native wallet test', identifier: 'urn:test:wallet-catalog' },
      entries: [{ identifier: 'urn:test:wallet-supplier', type: 'application/a2a-agent-card+json',
        url: `http://localhost:${port}/.well-known/agent-card.json` }],
    }));
    reservation.listen(0, '127.0.0.1');
    await once(reservation, 'listening');
    const buyerPort = reservation.address().port;
    await new Promise(resolve => reservation.close(resolve));
    origin = `http://127.0.0.1:${buyerPort}`;
    const buyer = spawn(process.env.AGENTCON_BUYER_EXE, [
      `--Buyer:ListenUrl=${origin}`, `--Buyer:CatalogUrl=${catalog}`,
      '--Buyer:NativeStructuredSupport=true', '--Buyer:ExchangeCaptureEnabled=true',
      '--Buyer:AllowLoopbackPresentationHttp=true',
    ], { env: { ...process.env, Buyer__SupplierControlToken: f.config.adminToken,
      Buyer__CopilotStateDirectory: join(directory, 'buyer-state'), OTEL_EXPORTER_OTLP_ENDPOINT: '' }, stdio: 'ignore' });
    t.after(() => { buyer.kill(); });
    t.after(() => rm(directory, { recursive: true, force: true }));
    ready = false;
    for (let i = 0; i < 100 && buyer.exitCode === null; i++) {
      try { ready = (await fetch(origin + '/health')).ok; } catch { /* Startup is not yet listening. */ }
      if (ready) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.equal(ready, true, 'Buyer did not start');
  }
  const context = 'wallet-http-context';
  async function send(data, taskId, metadata = {}) {
    const response = await fetch(origin + '/a2a', {
      method: 'POST', headers: { 'content-type': 'application/json', 'A2A-Version': '1.0',
        'A2A-Extensions': 'https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/v1' },
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'SendMessage', params: {
        message: { messageId: random(), role: 'ROLE_USER', contextId: context, ...(taskId && { taskId }),
          metadata,
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
  assert.equal(quoteTask.status.state, 'TASK_STATE_COMPLETED', JSON.stringify(quoteTask));
  assert.equal(quoteTask.artifacts[0].metadata['agentcon.llmCalls'], 0);
  if (route === 'buyer') assert.equal(quoteTask.status.message.metadata['agentcon.llmCalls'], 0);
  const quote = quoteTask.artifacts[0].parts[0].data;
  const order = { kind: 'purchase-order', schemaVersion: '1', demoOnly: true, quoteId: quote.quoteId, purchaseOrderId: 'wallet-po' };
  if (route === 'buyer') {
    const before = await (await fetch(supplierOrigin + '/health')).json();
    const tooExpensive = await send(order, undefined, { 'agentcon.maxTotalUsd': 1 });
    assert.equal(tooExpensive.status.state, 'TASK_STATE_INPUT_REQUIRED');
    assert.equal((await (await fetch(supplierOrigin + '/health')).json()).structuredRequests, before.structuredRequests);
    const changedTask = await send({ ...order, purchaseOrderId: 'tamper-check' });
    assert.equal(changedTask.status.state, 'TASK_STATE_AUTH_REQUIRED', JSON.stringify(changedTask));
    const changed = await send({ ...order, purchaseOrderId: 'tampered' }, changedTask.id);
    assert.equal(changed.status.state, 'TASK_STATE_AUTH_REQUIRED');
    assert.match(changed.status.message.parts[0].text, /Continuation rejected/);
  }
  const pending = await send(order);
  assert.equal(pending.status.state, 'TASK_STATE_AUTH_REQUIRED');
  assert.ok(!pending.artifacts?.length);
  const waiting = await send(order, pending.id);
  assert.equal(waiting.status.state, 'TASK_STATE_AUTH_REQUIRED');
  const requestsBeforeApproval = (await (await fetch(supplierOrigin + '/health')).json()).structuredRequests;
  if (route === 'buyer') {
    const dashboard = await (await fetch(origin + '/demo/state')).json();
    assert.ok(dashboard.capture.exchanges.some(exchange =>
      exchange.request.includes('SendStreamingMessage') && exchange.status === 'receiving' &&
      exchange.response.includes('TASK_STATE_AUTH_REQUIRED')),
    'Dashboard must show authorization while the seller stream is still open');
  }
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
  let complete;
  if (route === 'buyer') {
    for (let i = 0; i < 100; i++) {
      const response = await fetch(origin + '/a2a', { method: 'POST',
        headers: { 'content-type': 'application/json', 'A2A-Version': '1.0' },
        body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'GetTask', params: { id: pending.id } }) });
      const value = await response.json();
      assert.equal(value.error, undefined);
      complete = value.result;
      if (complete.status.state !== 'TASK_STATE_AUTH_REQUIRED') break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.equal((await (await fetch(supplierOrigin + '/health')).json()).structuredRequests, requestsBeforeApproval,
      'Wallet completion must not send another purchase message');
    const dashboard = await (await fetch(origin + '/demo/state')).json();
    assert.ok(dashboard.capture.tasks.some(task => task.agent === 'buyer' && task.id === pending.id &&
      task.state === 'TASK_STATE_COMPLETED'));
    assert.ok(dashboard.capture.exchanges.some(exchange => exchange.response.includes('statusUpdate') &&
      exchange.response.includes('TASK_STATE_COMPLETED')));
  } else {
    complete = await send({ ...order, quoteId: 'tampered', purchaseOrderId: 'tampered' }, pending.id);
  }
  assert.equal(complete.id, pending.id);
  assert.equal(complete.status.state, 'TASK_STATE_COMPLETED', JSON.stringify(complete));
  const receipt = complete.artifacts[0].parts[0].data;
  assert.equal(receipt.purchaseOrderId, 'wallet-po');
  assert.equal(receipt.quoteId, quote.quoteId);
  assert.equal(receipt.ageVerification, 'verified-demo-over-21');
  assert.equal(complete.artifacts.at(-1).metadata['agentcon.llmCalls'], 0);
  if (route === 'buyer') {
    assert.equal(complete.status.message.metadata['agentcon.llmCalls'], 0);
    const card = await (await fetch(origin + '/.well-known/agent-card.json')).json();
    assert.ok(card.defaultInputModes.includes('application/vnd.agentcon.demo.wine-rfq.v1+json'));
    const disabled = await fetch(origin + '/demo/native', { method: 'POST',
      headers: { 'content-type': 'application/json', origin }, body: JSON.stringify({ enabled: false }) });
    assert.equal(disabled.status, 200);
    const textCard = await (await fetch(origin + '/.well-known/agent-card.json')).json();
    assert.deepEqual(textCard.defaultInputModes, ['text/plain']);
    assert.equal((await send({ kind: 'rfq', schemaVersion: '1', demoOnly: true,
      wine: 'Barolo', vintage: 2019, cases: 1, deliveryCity: 'Toronto' })).status.state, 'TASK_STATE_FAILED');
    const supplierDisabled = await fetch(origin + '/demo/supplier-structures', { method: 'POST',
      headers: { 'content-type': 'application/json', origin },
      body: JSON.stringify({ identifier: 'urn:test:wallet-supplier', enabled: false }) });
    assert.equal(supplierDisabled.status, 200);
    const supplierCard = await (await fetch(supplierOrigin + '/.well-known/agent-card.json')).json();
    assert.deepEqual(supplierCard.defaultInputModes, ['text/plain']);
    assert.deepEqual(supplierCard.defaultOutputModes, ['text/plain']);
    assert.equal((await fetch(supplierOrigin + '/schemas/wine-v1.schema.json')).status, 404);
  }
  assert.equal((await (await fetch(supplierOrigin + '/health')).json()).inferenceAttempts, 0);
});
