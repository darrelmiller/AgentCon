import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import { createServer as netServer } from 'node:net';
import { spawn } from 'node:child_process';
import { exportJWK, generateKeyPair, importJWK, jwtVerify, SignJWT, decodeJwt, decodeProtectedHeader } from 'jose';
import * as EcdsaMultikey from '@digitalbazaar/ecdsa-multikey';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';
import { createIssuer } from '../server.mjs';
import { CONFIG_ID, GRANT, credentialType, random } from '../crypto.mjs';
import { loadConfig } from '../config.mjs';
import { escapeHtml } from '../ui.mjs';
import { createOffer } from '../create-offer.mjs';

const ORIGIN = 'https://agentcon-demo.azurewebsites.net';
async function key() {
  const pair = await generateKeyPair('ES256', { extractable: true });
  return { ...pair, publicJwk: await exportJWK(pair.publicKey), privateJwk: await exportJWK(pair.privateKey) };
}
async function fixture(t, overrides = {}) {
  const issuer = await key(), holder = await key();
  let time = 1_791_660_000;
  const config = {
    origin: ORIGIN, privateJwk: issuer.privateJwk, adminToken: random(),
    now: () => time, offerTtl: 600, tokenTtl: 600, nonceTtl: 300,
    maxEntries: 1000, rateLimit: 10000, rateWindow: 60, ...overrides,
  };
  const server = await createIssuer(config);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }));
  const local = `http://127.0.0.1:${server.address().port}`;
  async function request(path, options = {}) {
    const response = await fetch(local + path, options);
    const text = await response.text();
    const result = response.headers.get('content-type')?.includes('application/json') ? JSON.parse(text) : text;
    return { status: response.status, headers: response.headers, body: result };
  }
  const json = (path, value, token) => request(path, {
    method: 'POST', headers: { 'content-type': 'application/json', ...(token && { authorization: `Bearer ${token}` }) },
    body: JSON.stringify(value),
  });
  async function offer() {
    const created = await json('/admin/offers', {}, config.adminToken);
    assert.equal(created.status, 201);
    const raw = await request(new URL(created.body.credential_offer_uri).pathname);
    assert.equal(raw.status, 200);
    return { ...created.body, code: raw.body.grants[GRANT]['pre-authorized_code'], raw: raw.body };
  }
  const redeem = (code, extra = {}) => request('/token', {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: GRANT, 'pre-authorized_code': code, ...extra }).toString(),
  });
  async function session(extra) {
    const created = await offer(), token = await redeem(created.code, extra);
    assert.equal(token.status, 200);
    return { ...token.body, offer: created };
  }
  const proof = async (nonce, overrides = {}) => {
    const { header = {}, claims = {}, signingKey = holder.privateKey } = overrides;
    return new SignJWT({ aud: ORIGIN, iat: time, nonce, ...claims })
      .setProtectedHeader({ alg: 'ES256', typ: 'openid4vci-proof+jwt', jwk: holder.publicJwk, ...header })
      .sign(signingKey);
  };
  const issue = (session, jwt, final = false) => json('/credential', {
    credential_configuration_id: CONFIG_ID,
    ...(final ? { proofs: { jwt: [jwt] } } : { proof: { proof_type: 'jwt', jwt } }),
  }, session.access_token);
  return { config, holder, issuer, local, request, json, offer, redeem, session, proof, issue, advance: seconds => { time += seconds; } };
}

test('HTTP lifecycle discovers HTTPS issuer keys and signs selective demo claim for the wallet key', async t => {
  const f = await fixture(t);
  const metadata = await f.request('/.well-known/openid-credential-issuer');
  assert.equal(metadata.body.credential_issuer, ORIGIN);
  assert.equal(metadata.body.credential_configurations_supported[CONFIG_ID].format, 'dc+sd-jwt');
  assert.equal(metadata.body.nonce_endpoint, `${ORIGIN}/nonce`);
  for (const endpoint of ['/.well-known/oauth-authorization-server', '/.well-known/openid-configuration']) {
    const auth = await f.request(endpoint);
    assert.deepEqual(auth.body.grant_types_supported, [GRANT]);
    assert.equal(auth.body['pre-authorized_grant_anonymous_access_supported'], true);
  }
  const s = await f.session();
  assert.equal(s.token_type, 'Bearer');
  assert.equal(s.expires_in, 600);
  const jwt = await f.proof(s.c_nonce);
  const result = await f.issue(s, jwt);
  assert.equal(result.status, 200);
  assert.equal(result.headers.get('cache-control'), 'no-store');
  assert.equal(result.body.credentials.length, 1);
  const credential = result.body.credentials[0].credential;
  const [signed, disclosure, trailing] = credential.split('~');
  assert.equal(trailing, '');
  const jwks = (await f.request('/jwks')).body;
  const discovery = (await f.request('/.well-known/jwt-vc-issuer')).body;
  assert.deepEqual(discovery, { issuer: ORIGIN, jwks_uri: `${ORIGIN}/jwks` });
  assert.equal(jwks.keys[0].d, undefined);
  const header = decodeProtectedHeader(signed);
  assert.equal(header.typ, 'dc+sd-jwt');
  assert.equal(header.alg, 'ES256');
  assert.equal(header.kid, jwks.keys[0].kid);
  assert.match(header.kid, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(jwks.keys.length, 2, 'Keep the original key ID discoverable for existing credentials');
  assert.match(jwks.keys[1].kid, /^did:jwk:[A-Za-z0-9_-]+#0$/);
  assert.equal(jwks.keys[1].x, jwks.keys[0].x);
  assert.equal(jwks.keys[1].y, jwks.keys[0].y);
  const { payload } = await jwtVerify(signed, await importJWK(jwks.keys[0]), {
    issuer: ORIGIN, currentDate: new Date(f.config.now() * 1000),
  });
  assert.equal(payload.vct, credentialType(ORIGIN));
  assert.equal(metadata.body.credential_configurations_supported[CONFIG_ID].vct, payload.vct);
  const type = await f.request(new URL(payload.vct).pathname);
  assert.equal(type.status, 200);
  assert.equal(type.body.vct, payload.vct);
  assert.deepEqual(type.body.claims.map(claim => claim.path), [['over_21'], ['demo_only']]);
  assert.equal(payload.demo_only, true);
  assert.equal(payload.exp - payload.iat, 14 * 86400);
  assert.equal(payload.over_21, undefined);
  assert.deepEqual(payload.cnf.jwk, f.holder.publicJwk);
  const disclosed = JSON.parse(Buffer.from(disclosure, 'base64url').toString());
  assert.equal(disclosed[1], 'over_21');
  assert.equal(disclosed[2], true);
  assert.deepEqual(payload._sd, [createHash('sha256').update(disclosure).digest('base64url')]);
  await assert.rejects(jwtVerify(signed, await importJWK(jwks.keys[0]), {
    currentDate: new Date(payload.exp * 1000),
  }), { code: 'ERR_JWT_EXPIRED' });
  const sd = new SDJwtVcInstance({
    hasher: data => createHash('sha256').update(typeof data === 'string' ? data : Buffer.from(data)).digest(),
    verifier: async (data, signature) => {
      await jwtVerify(`${data}.${signature}`, await importJWK(jwks.keys[0]), { currentDate: new Date(f.config.now() * 1000) });
      return true;
    },
  });
  const verified = await sd.verify(credential, {
    expectedIssuer: ORIGIN, expectedVct: credentialType(ORIGIN), currentDate: f.config.now(),
  });
  assert.equal(verified.payload.over_21, true);
  assert.equal((await f.issue(s, jwt)).status, 401);
  assert.equal((await f.redeem(s.offer.code)).body.error, 'invalid_grant');
  assert.equal((await f.request(new URL(s.offer.credential_offer_uri).pathname)).status, 404);
});

test('browser wallet APIs support CORS preflight and authenticated issuance, but not admin access', async t => {
  const f = await fixture(t);
  const origin = 'https://wallet.example';
  const offer = await f.offer();
  const routes = [
    ...['/.well-known/openid-credential-issuer', '/.well-known/oauth-authorization-server',
      '/.well-known/openid-configuration', '/.well-known/jwt-vc-issuer', '/jwks',
      '/credential-types/age-over-21', new URL(offer.credential_offer_uri).pathname].map(path => [path, 'GET']),
    ...['/token', '/nonce', '/credential'].map(path => [path, 'POST']),
  ];
  for (const [path, method] of routes) {
    const preflight = await f.request(path, { method: 'OPTIONS', headers: {
      origin, 'access-control-request-method': method,
      'access-control-request-headers': 'authorization,content-type',
    } });
    assert.equal(preflight.status, 204, path);
    assert.equal(preflight.headers.get('access-control-allow-origin'), '*', path);
    assert.equal(preflight.headers.get('access-control-allow-methods'), method);
    assert.equal(preflight.headers.get('access-control-allow-credentials'), null);
    assert.match(preflight.headers.get('access-control-allow-headers'), /Authorization/);
    if (method === 'GET') {
      const result = await f.request(path, { headers: { origin } });
      assert.equal(result.status, 200, path);
      assert.equal(result.headers.get('access-control-allow-origin'), '*');
    }
  }
  const token = await f.request('/token', {
    method: 'POST', headers: { origin, 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: GRANT, 'pre-authorized_code': offer.code }),
  });
  assert.equal(token.status, 200);
  assert.equal(token.headers.get('access-control-allow-origin'), '*');
  const nonce = await f.request('/nonce', { method: 'POST', headers: { origin } });
  assert.equal(nonce.status, 200);
  const credential = await f.request('/credential', {
    method: 'POST', headers: { origin, 'content-type': 'application/json', authorization: `Bearer ${token.body.access_token}` },
    body: JSON.stringify({ credential_configuration_id: CONFIG_ID,
      proofs: { jwt: [await f.proof(nonce.body.c_nonce)] } }),
  });
  assert.equal(credential.status, 200);
  assert.equal(credential.headers.get('access-control-allow-origin'), '*');
  const unauthorized = await f.request('/credential', { method: 'POST', headers: { origin } });
  assert.equal(unauthorized.status, 401);
  assert.equal(unauthorized.headers.get('access-control-allow-origin'), '*');
  assert.match(unauthorized.headers.get('access-control-expose-headers'), /WWW-Authenticate/);
  const expired = await f.request(new URL(offer.credential_offer_uri).pathname, { headers: { origin } });
  assert.equal(expired.status, 404);
  assert.equal(expired.headers.get('access-control-allow-origin'), '*');
  for (const path of ['/admin/offers', '/admin/presentations', '/admin/presentations/' + random(), '/unknown']) {
    const denied = await f.request(path, { method: 'OPTIONS', headers: {
      origin, 'access-control-request-method': 'POST', 'access-control-request-headers': 'authorization',
    } });
    assert.notEqual(denied.status, 204);
    assert.equal(denied.headers.get('access-control-allow-origin'), null);
  }
  for (const headers of [
    { 'access-control-request-method': 'DELETE' },
    { 'access-control-request-method': 'POST', 'access-control-request-headers': 'x-admin-token' },
  ]) {
    assert.equal((await f.request('/token', { method: 'OPTIONS', headers: { origin, ...headers } })).status, 403);
  }
});

test('rate-limit responses remain readable by browser wallets', async t => {
  const f = await fixture(t, { rateLimit: 1 });
  await f.request('/jwks');
  const limited = await f.request('/jwks', { headers: { origin: 'https://wallet.example' } });
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get('access-control-allow-origin'), '*');
  assert.ok(limited.headers.get('retry-after'));
});

test('final-style proofs and anonymous nonce endpoint work without a token nonce', async t => {
  const f = await fixture(t);
  const challenge = await f.request('/nonce', {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: '',
  });
  assert.equal(challenge.status, 200);
  const s = await f.session();
  assert.equal((await f.issue(s, await f.proof(challenge.body.c_nonce), true)).status, 200);
  const another = await f.session();
  const replay = await f.issue(another, await f.proof(challenge.body.c_nonce), true);
  assert.equal(replay.body.error, 'invalid_nonce');
  assert.ok(replay.body.c_nonce);
});

test('public-key DID references resolve locally for did:jwk and P-256 did:key', async t => {
  const f = await fixture(t);
  const encoded = Buffer.from(JSON.stringify(f.holder.publicJwk)).toString('base64url');
  const pair = await EcdsaMultikey.fromJwk({ jwk: f.holder.publicJwk });
  const exported = await pair.export({ publicKey: true });
  for (const kid of [`did:jwk:${encoded}#0`, `did:key:${exported.publicKeyMultibase}#${exported.publicKeyMultibase}`]) {
    const s = await f.session();
    const jwt = await f.proof(s.c_nonce, { header: { jwk: undefined, kid } });
    const result = await f.issue(s, jwt);
    assert.equal(result.status, 200);
    assert.deepEqual(decodeJwt(result.body.credentials[0].credential.split('~')[0]).cnf.jwk, f.holder.publicJwk);
  }
});

test('proof validation rejects signature, audience, typ, stale/future timestamps and unsafe key references', async t => {
  const f = await fixture(t);
  const wrong = await key();
  const cases = [
    { signingKey: wrong.privateKey },
    { claims: { aud: 'https://attacker.invalid' } },
    { claims: { aud: [ORIGIN, 'https://attacker.invalid'] } },
    { header: { typ: 'JWT' } },
    { claims: { iat: f.config.now() - 301 } },
    { claims: { iat: f.config.now() + 6 } },
    { claims: { iat: undefined } },
    { claims: { exp: f.config.now() - 1 } },
    { claims: { nbf: f.config.now() + 60 } },
    { claims: { nbf: f.config.now() - 0.5 } },
    { claims: { iss: 'unexpected-client' } },
    { header: { jku: 'https://attacker.invalid/jwks' } },
    { header: { x5c: [] } },
    { header: { jwk: undefined, kid: 'did:web:attacker.invalid#key' } },
    { header: { jwk: undefined, kid: 'https://attacker.invalid/key' } },
    { header: { kid: 'did:key:zInvalid#zInvalid' } },
    { header: { jwk: f.holder.privateJwk } },
    { header: { jwk: { ...f.holder.publicJwk, crv: 'P-384' } } },
    { header: { jwk: { ...f.holder.publicJwk, x: 'A'.repeat(43), y: 'A'.repeat(43) } } },
    { header: { jwk: undefined, kid: `did:jwk:${Buffer.from(JSON.stringify(f.holder.publicJwk)).toString('base64url')}#wrong` } },
  ];
  for (const [index, bad] of cases.entries()) {
    const s = await f.session();
    const failed = await f.issue(s, await f.proof(s.c_nonce, bad));
    assert.equal(failed.status, 400, `case ${index}`);
    assert.equal(failed.body.error, 'invalid_proof', `case ${index}`);
    assert.notEqual(failed.body.c_nonce, s.c_nonce);
    assert.equal((await f.issue(s, await f.proof(failed.body.c_nonce))).status, 200);
  }
});

test('nonce failure rotates challenge; old, foreign and expired challenges cannot issue', async t => {
  const f = await fixture(t);
  const s = await f.session(), foreign = await f.session();
  for (const value of [random(), foreign.c_nonce, s.c_nonce]) {
    if (value === s.c_nonce) f.advance(301);
    const failed = await f.issue(s, await f.proof(value));
    assert.equal(failed.body.error, 'invalid_nonce');
    assert.equal(failed.body.c_nonce_expires_in, 300);
  }
  const challenge = await f.request('/nonce', { method: 'POST', headers: { authorization: `Bearer ${s.access_token}` } });
  assert.equal(challenge.status, 200);
  assert.equal((await f.issue(s, await f.proof(challenge.body.c_nonce))).status, 200);
});

test('client_id is optional, but proof issuer must match if present', async t => {
  const f = await fixture(t);
  const s = await f.session({ client_id: 'wallet-client' });
  const failed = await f.issue(s, await f.proof(s.c_nonce, { claims: { iss: 'different-client' } }));
  assert.equal(failed.body.error, 'invalid_proof');
  assert.equal((await f.issue(s, await f.proof(failed.body.c_nonce, { claims: { iss: 'wallet-client' } }))).status, 200);
});

test('proof diagnostics distinguish unsupported algorithms and anonymous iss without echoing proof data', async t => {
  const f = await fixture(t);
  for (const [overrides, message] of [
    [{ header: { alg: 'unsupported-wallet-algorithm' } }, /supports ES256/],
    [{ header: { typ: 'JWT' } }, /typ must be openid4vci-proof\+jwt/],
    [{ claims: { iss: 'did:key:wallet-identity-must-not-be-echoed' } }, /iss must be omitted/],
    [{ claims: { aud: 'https://wrong-audience.invalid' } }, /aud must exactly match/],
    [{ claims: { iat: f.config.now() * 1000 } }, /integer seconds/],
  ]) {
    const session = await f.session();
    const valid = await f.proof(session.c_nonce, { claims: overrides.claims });
    // An unsupported alg must be diagnosed before attempting signature verification.
    const proof = overrides.header
      ? [Buffer.from(JSON.stringify({ ...decodeProtectedHeader(valid), ...overrides.header })).toString('base64url'),
        ...valid.split('.').slice(1)].join('.')
      : valid;
    const failed = await f.issue(session, proof, true);
    assert.equal(failed.status, 400);
    assert.equal(failed.body.error, 'invalid_proof');
    assert.match(failed.body.error_description, message);
    assert.doesNotMatch(failed.body.error_description, /wallet-identity-must-not-be-echoed|wrong-audience/);
    assert.notEqual(failed.body.c_nonce, session.c_nonce);
    assert.equal((await f.issue(session, await f.proof(failed.body.c_nonce), true)).status, 200);
  }
});

test('key-reference diagnostics distinguish wallet header failures without echoing public keys', async t => {
  const f = await fixture(t);
  const cases = [
    [{ kid: 'did:key:redacted-identity' }, /base58btc fingerprint/],
    [{ jwk: undefined }, /no jwk or kid/],
    [{ jwk: undefined, kid: 'did:key:zRedacted' }, /matching #fingerprint/],
    [{ jwk: undefined, kid: 'did:jwk:redacted#wrong' }, /must end with #0/],
    [{ jwk: undefined, kid: 'did:web:redacted.example#0' }, /method is unsupported/],
    [{ jwk: undefined, kid: 'did:key:zBadKey#zBadKey' }, /cannot be decoded/],
    [{ jwk: { ...f.holder.publicJwk, crv: 'P-384' } }, /EC P-256/],
    [{ jwk: f.holder.privateJwk }, /private key material/],
    [{ x5c: ['redacted-certificate'] }, /Certificate-based/],
    [{ jku: 'https://redacted.example/keys' }, /Remote proof key URLs/],
    [{ jwk: { ...f.holder.publicJwk, x: 'A'.repeat(43), y: 'A'.repeat(43) } }, /invalid coordinates/],
  ];
  for (const [header, expected] of cases) {
    const session = await f.session();
    const response = await f.issue(session, await f.proof(session.c_nonce, { header }));
    assert.equal(response.status, 400);
    assert.equal(response.body.error, 'invalid_proof');
    assert.match(response.body.error_description, expected);
    assert.doesNotMatch(response.body.error_description, /redacted|zBadKey/);
  }
});

test('accepts a wallet JWK plus matching did:key and rejects a conflicting DID key', async t => {
  const f = await fixture(t);
  const pair = await EcdsaMultikey.fromJwk({ jwk: f.holder.publicJwk });
  const exported = await pair.export({ publicKey: true });
  const kid = `did:key:${exported.publicKeyMultibase}#${exported.publicKeyMultibase}`;
  const session = await f.session();
  const matching = await f.proof(session.c_nonce, { header: { kid } });
  assert.equal((await f.issue(session, matching, true)).status, 200);

  const other = await generateKeyPair('ES256', { extractable: true });
  const otherPair = await EcdsaMultikey.fromJwk({ jwk: await exportJWK(other.publicKey) });
  const otherKey = await otherPair.export({ publicKey: true });
  const conflictingSession = await f.session();
  const conflicting = await f.proof(conflictingSession.c_nonce, {
    header: { kid: `did:key:${otherKey.publicKeyMultibase}#${otherKey.publicKeyMultibase}` },
  });
  const rejected = await f.issue(conflictingSession, conflicting, true);
  assert.equal(rejected.status, 400);
  assert.match(rejected.body.error_description, /jwk does not match/);
});

test('private offer page reports issuer progress after redemption without retaining proof or credential data', async t => {
  const f = await fixture(t);
  const offer = await f.offer();
  const path = new URL(offer.offer_page).pathname;
  const page = () => f.request(path);
  assert.match((await page()).body, /Offer fetched/);
  const session = (await f.redeem(offer.code)).body;
  const redeemed = await page();
  assert.equal(redeemed.status, 200);
  assert.match(redeemed.body, /Token redeemed/);
  assert.doesNotMatch(redeemed.body, /data:image\/png|openid-credential-offer:/);
  assert.equal((await f.request(new URL(offer.credential_offer_uri).pathname)).status, 404);
  const proof = await f.proof(session.c_nonce, { header: { kid: 'did:key:private-test-identity' } });
  const failed = await f.issue(session, proof);
  const rejected = await page();
  assert.match(rejected.body, /Credential request rejected/);
  assert.match(rejected.body, /base58btc fingerprint/);
  for (const secret of [session.access_token, session.c_nonce, proof,
    f.holder.publicJwk.x, 'private-test-identity', failed.body.c_nonce, offer.code]) {
    assert.equal(rejected.body.includes(secret), false);
  }
  assert.equal(rejected.headers.get('access-control-allow-origin'), null);
  assert.equal(rejected.headers.get('cache-control'), 'no-store');
  assert.equal(rejected.headers.get('referrer-policy'), 'no-referrer');
  const issued = await f.issue(session, await f.proof(failed.body.c_nonce));
  assert.equal(issued.status, 200);
  const completed = await page();
  assert.match(completed.body, /Credential issued/);
  assert.match(completed.body, /Confirm that the wallet stored it successfully/);
  assert.equal(completed.body.includes(issued.body.credentials[0].credential), false);
  assert.equal((await f.request('/offers/' + random())).status, 404);
  f.advance(601);
  assert.equal((await page()).status, 404);
});

test('proof timestamps inside the advertised validation window are accepted', async t => {
  const f = await fixture(t);
  const s = await f.session();
  const jwt = await f.proof(s.c_nonce, {
    claims: { iat: f.config.now() - 60, exp: f.config.now() + 600 },
  });
  assert.equal((await f.issue(s, jwt)).status, 200);
});

test('unsigned and wrong-algorithm JWTs and multi-proof requests are rejected', async t => {
  const f = await fixture(t);
  const s = await f.session();
  const valid = await f.proof(s.c_nonce);
  const [header, payload] = valid.split('.');
  const unsigned = `${Buffer.from(JSON.stringify({ ...decodeProtectedHeader(valid), alg: 'none' })).toString('base64url')}.${payload}.`;
  const wrongAlg = `${Buffer.from(JSON.stringify({ ...decodeProtectedHeader(valid), alg: 'HS256' })).toString('base64url')}.${payload}.invalid`;
  for (const jwt of [unsigned, wrongAlg, `${header}.${payload}.invalid`]) {
    assert.equal((await f.issue(s, jwt)).body.error, 'invalid_proof');
  }
  const multi = await f.json('/credential', {
    credential_configuration_id: CONFIG_ID, proofs: { jwt: [valid, valid] },
  }, s.access_token);
  assert.equal(multi.body.error, 'invalid_proof');
  assert.equal((await f.issue(s, await f.proof(multi.body.c_nonce), true)).status, 200);
});

test('offers, pre-authorized codes and access tokens expire', async t => {
  const f = await fixture(t);
  const offer = await f.offer();
  f.advance(600);
  assert.equal((await f.request(new URL(offer.offer_page).pathname)).status, 404);
  assert.equal((await f.redeem(offer.code)).body.error, 'invalid_grant');
  const s = await f.session();
  f.advance(600);
  assert.equal((await f.issue(s, await f.proof(s.c_nonce))).body.error, 'invalid_token');
  assert.equal((await f.request('/nonce', { method: 'POST', headers: { authorization: `Bearer ${s.access_token}` } })).status, 401);
});

test('parallel code redemption and credential requests have exactly one winner', async t => {
  const f = await fixture(t);
  const offer = await f.offer();
  const redemption = await Promise.all(Array.from({ length: 12 }, () => f.redeem(offer.code)));
  assert.equal(redemption.filter(r => r.status === 200).length, 1);
  assert.equal(redemption.filter(r => r.body.error === 'invalid_grant').length, 11);
  const s = redemption.find(r => r.status === 200).body;
  const jwt = await f.proof(s.c_nonce);
  const requests = await Promise.all(Array.from({ length: 12 }, () => f.issue(s, jwt)));
  assert.equal(requests.filter(r => r.status === 200).length, 1);
  assert.ok(requests.filter(r => r.status !== 200).every(r => [401, 409].includes(r.status)));
});

test('one anonymous nonce cannot authorize parallel issuance across tokens', async t => {
  const f = await fixture(t);
  const challenge = (await f.request('/nonce', { method: 'POST' })).body.c_nonce;
  const sessions = await Promise.all(Array.from({ length: 6 }, () => f.session()));
  const jwt = await f.proof(challenge);
  const requests = await Promise.all(sessions.map(s => f.issue(s, jwt)));
  assert.equal(requests.filter(r => r.status === 200).length, 1);
  assert.equal(requests.filter(r => r.body.error === 'invalid_nonce').length, 5);
});

test('admin authorization, OAuth errors and request bounds do not leak secrets', async t => {
  const f = await fixture(t);
  assert.equal((await f.json('/admin/offers', {})).status, 401);
  assert.equal((await f.json(`/admin/offers?token=${f.config.adminToken}`, {})).status, 401);
  assert.equal((await f.json('/admin/offers', {}, random())).status, 401);
  assert.equal((await f.json('/admin/offers', { over_21: false }, f.config.adminToken)).status, 400);
  assert.equal((await f.request('/token')).status, 405);
  assert.equal((await f.json('/token', {})).status, 415);
  assert.equal((await f.redeem(random(), { grant_type: 'password' })).body.error, 'unsupported_grant_type');
  assert.equal((await f.redeem(random())).body.error, 'invalid_grant');
  const malformed = await f.request('/token', {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: `grant_type=${encodeURIComponent(GRANT)}&grant_type=other`,
  });
  assert.equal(malformed.body.error, 'invalid_request');
  const s = await f.session();
  assert.equal((await f.json('/credential', { credential_configuration_id: 'unknown' }, s.access_token)).body.error, 'unknown_credential_configuration');
  const duplicate = await f.json('/credential', {
    credential_configuration_id: CONFIG_ID, proof: { proof_type: 'jwt', jwt: 'invalid' }, proofs: { jwt: ['invalid'] },
  }, s.access_token);
  assert.equal(duplicate.body.error, 'invalid_proof');
  const oversized = await f.request('/admin/offers', {
    method: 'POST', headers: { authorization: `Bearer ${f.config.adminToken}`, 'content-type': 'application/json' },
    body: JSON.stringify({ data: 'x'.repeat(17000) }),
  });
  assert.equal(oversized.status, 413);
  const invalidJson = await f.request('/admin/offers', {
    method: 'POST', headers: { authorization: `Bearer ${f.config.adminToken}`, 'content-type': 'application/json' }, body: '{',
  });
  assert.equal(invalidJson.status, 400);
  const safe = await f.request('/health');
  assert.deepEqual(safe.body, { status: 'ok', demo_only: true });
  assert.equal(JSON.stringify(safe.body).includes(f.config.privateJwk.d), false);
  assert.equal(JSON.stringify(duplicate.body).includes(f.config.adminToken), false);
});

test('bounded maps clean up expired state; rate limiting remains bounded', async t => {
  const f = await fixture(t, { maxEntries: 2 });
  await f.offer(); await f.offer();
  assert.equal((await f.json('/admin/offers', {}, f.config.adminToken)).status, 503);
  f.advance(600);
  assert.equal((await f.json('/admin/offers', {}, f.config.adminToken)).status, 201);
  const limited = await fixture(t, { rateLimit: 2 });
  assert.equal((await limited.request('/jwks')).status, 200);
  assert.equal((await limited.request('/jwks')).status, 200);
  const rejected = await limited.request('/jwks');
  assert.equal(rejected.status, 429);
  assert.equal(rejected.headers.get('retry-after'), '60');
  assert.equal((await limited.request('/health')).status, 200);
  limited.advance(60);
  assert.equal((await limited.request('/jwks')).status, 200);
});

test('chunked oversized payload returns a bounded HTTP error without resetting the response', async t => {
  const f = await fixture(t);
  const response = await new Promise((resolve, reject) => {
    const req = httpRequest(`${f.local}/admin/offers`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${f.config.adminToken}` },
    }, res => {
      let text = '';
      res.on('data', chunk => { text += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, body: JSON.parse(text) }));
    });
    req.on('error', reject);
    req.write('{"padding":"');
    req.write('x'.repeat(17000));
    req.end('"}');
  });
  assert.equal(response.status, 413);
  assert.equal(response.body.error, 'invalid_request');
});

test('offer UI has local QR, full theme, escaped content and no CDN', async t => {
  const f = await fixture(t);
  const root = await f.request('/');
  assert.equal(root.status, 200);
  assert.match(root.headers.get('content-type'), /text\/html/);
  assert.match(root.body, /DEMO ONLY/);
  assert.match(root.body, /no public offer creation/);
  assert.doesNotMatch(root.body, /openid-credential-offer|<form|data:image/);
  assert.equal(root.body.includes(f.config.adminToken), false);
  const offer = await f.offer();
  const page = await f.request(new URL(offer.offer_page).pathname);
  assert.equal(page.status, 200);
  assert.match(page.body, /data:image\/png;base64,/);
  assert.match(page.body, /DEMO ONLY/);
  assert.match(page.body, /--cp-panel-strong:/);
  assert.match(page.body, /"Segoe UI", Aptos, Calibri/);
  assert.match(page.body, /openid-credential-offer:\/\/\?credential_offer_uri=/);
  assert.doesNotMatch(page.body, /<script src|<link|src="https:/);
  assert.equal(page.headers.get('referrer-policy'), 'no-referrer');
  assert.match(page.headers.get('content-security-policy'), /frame-ancestors 'none'/);
  assert.equal(escapeHtml('<script>&"\'</script>'), '&lt;script&gt;&amp;&quot;&#39;&lt;/script&gt;');
});

test('startup configuration rejects unsafe origins, weak admin tokens and missing/invalid keys', async () => {
  const issuer = await key();
  const valid = { ISSUER_BASE_URL: ORIGIN, ISSUER_ADMIN_TOKEN: random(), ISSUER_PRIVATE_JWK: JSON.stringify(issuer.privateJwk) };
  const config = await loadConfig(valid);
  assert.equal(config.origin, ORIGIN);
  assert.equal(config.port, 3000);
  for (const base of ['http://localhost:3000', 'https://example.org/path', 'https://user:pass@example.org', 'https://example.org?x=1', 'not-url']) {
    await assert.rejects(loadConfig({ ...valid, ISSUER_BASE_URL: base }), /HTTPS origin/);
  }
  await assert.rejects(loadConfig({ ...valid, ISSUER_ADMIN_TOKEN: 'short' }), /ISSUER_ADMIN_TOKEN/);
  await assert.rejects(loadConfig({ ...valid, ISSUER_PRIVATE_JWK: undefined }), /exactly one/);
  await assert.rejects(loadConfig({ ...valid, ISSUER_PRIVATE_JWK_PATH: 'another' }), /exactly one/);
  await assert.rejects(loadConfig({ ...valid, ISSUER_PRIVATE_JWK: '{SECRET_INVALID' }), /valid private ES256/);
  await assert.rejects(loadConfig({ ...valid, ISSUER_PRIVATE_JWK: JSON.stringify(issuer.publicJwk) }), /valid private ES256/);
  await assert.rejects(loadConfig({ ...valid, PORT: 'invalid' }), /PORT/);
});

test('server entrypoint starts from environment and responds safely on PORT', async t => {
  const issuer = await key();
  const reserve = netServer();
  reserve.listen(0, '127.0.0.1');
  await once(reserve, 'listening');
  const port = reserve.address().port;
  await new Promise(resolve => reserve.close(resolve));
  const child = spawn(process.execPath, ['server.mjs'], {
    cwd: new URL('../', import.meta.url),
    env: { ...process.env, PORT: String(port), ISSUER_BASE_URL: ORIGIN,
      ISSUER_ADMIN_TOKEN: random(), ISSUER_PRIVATE_JWK: JSON.stringify(issuer.privateJwk),
      ISSUER_PRIVATE_JWK_PATH: '' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = '';
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('Issuer entrypoint startup timed out')), 10000);
    child.stdout.on('data', chunk => {
      output += chunk;
      if (output.includes('listening on port')) { clearTimeout(timeout); resolve(); }
    });
    child.on('error', error => { clearTimeout(timeout); reject(error); });
    child.on('exit', code => { clearTimeout(timeout); reject(new Error(`Issuer entrypoint exited: ${code}`)); });
  });
  const health = await fetch(`http://127.0.0.1:${port}/health`);
  assert.equal(health.status, 200);
  assert.deepEqual(await health.json(), { status: 'ok', demo_only: true });
  assert.equal(output.includes(issuer.privateJwk.d), false);
  child.kill();
  await once(child, 'exit');
});

test('local admin client uses a header secret and returns only the private page URL', async t => {
  const f = await fixture(t);
  const env = { ISSUER_BASE_URL: ORIGIN, ISSUER_ADMIN_TOKEN: f.config.adminToken };
  const page = await createOffer(env, (url, options) => {
    assert.equal(url, `${ORIGIN}/admin/offers`);
    assert.equal(options.headers.Authorization, `Bearer ${f.config.adminToken}`);
    assert.equal(options.redirect, 'error');
    assert.equal(options.body, '{}');
    assert.equal(url.includes(f.config.adminToken), false);
    return fetch(`${f.local}/admin/offers`, options);
  });
  assert.match(page, /^https:\/\/agentcon-demo\.azurewebsites\.net\/offers\/[A-Za-z0-9_-]{43}$/);
  assert.equal((await f.request(new URL(page).pathname)).status, 200);
  await assert.rejects(createOffer({ ...env, ISSUER_BASE_URL: 'http://localhost' }), /HTTPS origin/);
  await assert.rejects(createOffer({ ...env, ISSUER_ADMIN_TOKEN: 'weak' }), /ISSUER_ADMIN_TOKEN/);
  await assert.rejects(createOffer(env, async () => ({ status: 401 })), /HTTP 401/);
  await assert.rejects(createOffer(env, async () => {
    throw new Error(`Secret ${env.ISSUER_ADMIN_TOKEN}`);
  }), error => !error.message.includes(env.ISSUER_ADMIN_TOKEN));
  await assert.rejects(createOffer(env, async () => ({
    status: 201, json: async () => ({ offer_page: 'https://attacker.invalid/offers/invalid' }),
  })), /invalid offer page/);
});
