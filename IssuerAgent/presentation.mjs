import { createHash, createPrivateKey, sign } from 'node:crypto';
import { decodeProtectedHeader, importJWK, jwtVerify } from 'jose';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';
import { credentialType, digest, random, LEGACY_VCT } from './crypto.mjs';
import { publicP256 } from './config.mjs';
import { presentationPage } from './ui.mjs';

export function presentationService(config, issuerJwk) {
  const publicKey = publicP256({
    kty: issuerJwk.kty, crv: issuerJwk.crv, x: issuerJwk.x, y: issuerJwk.y,
  });
  const requestKid = `did:jwk:${Buffer.from(JSON.stringify(publicKey)).toString('base64url')}#0`;
  const trustedKids = [issuerJwk.kid, requestKid];
  const requestKey = createPrivateKey({ key: config.privateJwk, format: 'jwk' });
  const sessions = new Map();
  const ttl = 600;
  const hasher = (data, algorithm) => {
    if (algorithm !== 'sha-256') throw new Error('Unsupported disclosure hash');
    return createHash('sha256').update(typeof data === 'string' ? data : Buffer.from(data)).digest();
  };
  function cleanup() {
    for (const [id, session] of sessions) if (session.expiresAt <= config.now()) sessions.delete(id);
  }
  function get(id) {
    cleanup();
    const session = sessions.get(id);
    if (!session) throw new Error('Presentation unavailable or expired');
    return session;
  }
  return {
    create(taskId, contextId) {
      cleanup();
      if (sessions.size >= config.maxEntries) throw new Error('Presentation capacity reached');
      const id = random(), nonce = random(), state = random();
      const responseUri = `${config.origin}/presentations/${id}/response`;
      const clientId = `decentralized_identifier:${requestKid.split('#')[0]}`;
      const session = { taskId, contextId, nonce, state, clientId, expiresAt: config.now() + ttl, verified: false, busy: false };
      const request = {
        iss: clientId, aud: 'https://self-issued.me/v2', iat: config.now(), exp: session.expiresAt,
        client_id: clientId, response_type: 'vp_token', response_mode: 'direct_post',
        response_uri: responseUri, nonce, state,
        dcql_query: { credentials: [{
          id: 'age', format: 'dc+sd-jwt', meta: { vct_values: [credentialType(config.origin), LEGACY_VCT] },
          claims: [{ path: ['over_21'], values: [true] }],
        }] },
        client_metadata: {
          client_name: 'AgentCon wine supplier (simulated purchase)',
          vp_formats_supported: { 'dc+sd-jwt': { 'sd-jwt_alg_values': ['ES256'], 'kb-jwt_alg_values': ['ES256'] } },
        },
      };
      sessions.set(id, session);
      const protectedHeader = { alg: 'ES256', typ: 'oauth-authz-req+jwt', kid: requestKid };
      const signingInput = [protectedHeader, request]
        .map(value => Buffer.from(JSON.stringify(value)).toString('base64url')).join('.');
      session.request = `${signingInput}.${sign('sha256', Buffer.from(signingInput), {
        key: requestKey, dsaEncoding: 'ieee-p1363',
      }).toString('base64url')}`;
      const requestUri = `${config.origin}/presentations/${id}/request`;
      return {
        id, taskId, contextId, expiresAt: session.expiresAt,
        authorizationRequest: { client_id: clientId, request_uri: requestUri },
        presentationUrl: `${config.origin}/presentations/${id}`,
        walletUri: `openid4vp://?${new URLSearchParams({ client_id: clientId, request_uri: requestUri, request_uri_method: 'get' })}`,
      };
    },
    status(id) {
      const session = get(id);
      return { taskId: session.taskId, contextId: session.contextId, verified: session.verified, expiresAt: session.expiresAt };
    },
    request(id) { return get(id).request; },
    async page(id) {
      const session = get(id);
      const requestUri = `${config.origin}/presentations/${id}/request`;
      const uri = `openid4vp://?${new URLSearchParams({ client_id: session.clientId, request_uri: requestUri, request_uri_method: 'get' })}`;
      return presentationPage(uri, config.origin, session.expiresAt);
    },
    async verify(id, input) {
      const session = get(id);
      if (session.verified || session.busy) throw new Error('Presentation already submitted');
      if (input.state !== session.state) throw new Error('Invalid state');
      let tokens;
      try { tokens = JSON.parse(input.vp_token); } catch { throw new Error('Expected DCQL vp_token'); }
      if (!tokens || Object.keys(tokens).length !== 1 || !Array.isArray(tokens.age) ||
          tokens.age.length !== 1 || typeof tokens.age[0] !== 'string' || tokens.age[0].length > 12000) {
        throw new Error('Expected one age presentation');
      }
      session.busy = true;
      try {
        const presentation = tokens.age[0];
        const [credential] = presentation.split('~');
        const header = decodeProtectedHeader(credential);
        if (header.alg !== 'ES256' || header.typ !== 'dc+sd-jwt' || !trustedKids.includes(header.kid) ||
            header.jwk || header.jku || header.x5u || header.x5c) throw new Error('Untrusted issuer key');
        const { payload } = await jwtVerify(credential, await importJWK(issuerJwk, 'ES256'), {
          algorithms: ['ES256'], issuer: config.origin, currentDate: new Date(config.now() * 1000),
        });
        if (!Number.isSafeInteger(payload.exp) || !Number.isSafeInteger(payload.iat) ||
            payload.iat > config.now() || payload.exp <= config.now() ||
            ![credentialType(config.origin), LEGACY_VCT].includes(payload.vct) ||
            payload.demo_only !== true) throw new Error('Invalid issuer claims');
        const holder = publicP256(payload.cnf?.jwk);
        const sd = new SDJwtVcInstance({
          hasher,
          verifier: async (data, signature) => {
            await jwtVerify(`${data}.${signature}`, await importJWK(issuerJwk, 'ES256'), {
              algorithms: ['ES256'], issuer: config.origin, currentDate: new Date(config.now() * 1000),
            });
            return true;
          },
        });
        const disclosed = await sd.verify(presentation, { requiredClaimKeys: ['over_21'] });
        if (disclosed.payload.over_21 !== true) throw new Error('Required claim not disclosed');
        const segments = presentation.split('~');
        const kb = segments.pop();
        if (!kb) throw new Error('Holder binding required');
        const kbHeader = decodeProtectedHeader(kb);
        if (kbHeader.alg !== 'ES256' || kbHeader.typ !== 'kb+jwt' ||
            kbHeader.jwk || kbHeader.jku || kbHeader.x5u || kbHeader.x5c) throw new Error('Invalid holder header');
        const verified = await jwtVerify(kb, await importJWK(holder, 'ES256'), {
          algorithms: ['ES256'], audience: session.clientId, currentDate: new Date(config.now() * 1000),
        });
        const claims = verified.payload;
        if (claims.aud !== session.clientId || claims.nonce !== session.nonce ||
            !Number.isSafeInteger(claims.iat) || claims.iat > config.now() + 5 || claims.iat < config.now() - 300 ||
            claims.sd_hash !== digest(`${segments.join('~')}~`)) throw new Error('Invalid holder binding');
        if (session.expiresAt <= config.now()) throw new Error('Presentation expired');
        session.verified = true;
      } finally {
        session.busy = false;
      }
    },
  };
}
