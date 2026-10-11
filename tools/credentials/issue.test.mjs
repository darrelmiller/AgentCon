import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { createKey, credentialService, issueCredential, provision } from './issue.mjs';

const config = JSON.parse(await readFile(new URL('./age-credential.json', import.meta.url), 'utf8'));

test('signs a labelled age credential, binds the holder, and discloses only the configured personal claim', async () => {
  const issuer = await createKey();
  const holder = await createKey();
  const { credential, iss } = await issueCredential(issuer, holder.publicJwk, config);
  const service = credentialService({ publicJwk: issuer.publicJwk });
  const presentation = await service.present(credential, { over_21: true });
  const result = await service.verify(presentation, {
    expectedIssuer: iss,
    expectedVct: config.vct,
    requiredClaimKeys: ['over_21'],
  });
  assert.equal(result.payload.over_21, true);
  assert.equal(result.payload.demo_only, true);
  assert.equal(result.payload.exp - result.payload.iat, 1209600);
  assert.deepEqual(result.payload.cnf.jwk, holder.publicJwk);
  assert.equal(result.header.alg, 'ES256');
  assert.equal(result.header.typ, 'dc+sd-jwt');
  assert.equal(result.header.kid, `${iss}#0`);
  assert.ok(iss.startsWith('did:jwk:'));
  assert.deepEqual(Object.keys(result.payload).sort(),
    ['cnf', 'demo_only', 'exp', 'iat', 'iss', 'over_21', 'title', 'vct'].sort());
  const undisclosed = await service.present(credential, {});
  await assert.rejects(service.verify(undisclosed, { requiredClaimKeys: ['over_21'] }));
  assert.equal((await service.verify(undisclosed)).payload.over_21, undefined);
});

test('rejects changed signatures, wrong issuers, and expired credentials', async () => {
  const issuer = await createKey();
  const holder = await createKey();
  const { credential } = await issueCredential(issuer, holder.publicJwk, config);
  const service = credentialService({ publicJwk: issuer.publicJwk });
  const parts = credential.split('~');
  const jwt = parts[0].split('.');
  const signature = Buffer.from(jwt[2], 'base64url');
  signature[0] ^= 1;
  jwt[2] = signature.toString('base64url');
  parts[0] = jwt.join('.');
  await assert.rejects(service.verify(parts.join('~')));
  await assert.rejects(service.verify(credential, { expectedIssuer: 'urn:wrong-issuer' }));
  const otherIssuer = await createKey();
  await assert.rejects(credentialService(otherIssuer).verify(credential));
  const expired = await issueCredential(issuer, holder.publicJwk, config,
    Math.floor(Date.now() / 1000) - config.validity_seconds - 10);
  await assert.rejects(service.verify(expired.credential), /expired/i);
  await assert.rejects(issueCredential(issuer, holder.publicJwk, { ...config, demo_only: false }));
  await assert.rejects(issueCredential(issuer, holder.privateJwk, config), /public P-256/);
});

test('persists separate issuances outside Git without exposing private keys in public files', async () => {
  const state = await mkdtemp(join(tmpdir(), 'agentcon-credential-test-'));
  try {
    const first = await provision(state);
    const second = await provision(state);
    assert.notEqual(first, second);
    const privateJwk = JSON.parse(await readFile(join(first, 'issuer-private.jwk.json'), 'utf8'));
    const jwks = JSON.parse(await readFile(join(first, 'issuer-public.jwks.json'), 'utf8'));
    assert.equal(typeof privateJwk.d, 'string');
    assert.equal(jwks.keys[0].d, undefined);
    assert.equal(jwks.keys[0].kid, privateJwk.kid);
    const credential = (await readFile(join(first, 'age-credential.sd-jwt'), 'utf8')).trim();
    assert.equal((await credentialService({ publicJwk: jwks.keys[0] }).verify(credential)).payload.over_21, true);
    await assert.rejects(provision(fileURLToPath(new URL('../../', import.meta.url))), /outside the repository/);
    await assert.rejects(provision('relative-path'));
  } finally {
    await rm(state, { recursive: true });
  }
});
