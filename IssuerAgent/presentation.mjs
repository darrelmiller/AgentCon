import { createHash, createPrivateKey, sign } from 'node:crypto';
import { decodeProtectedHeader, importJWK, jwtVerify } from 'jose';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';
import * as EcdsaMultikey from '@digitalbazaar/ecdsa-multikey';
import { credentialType, digest, random, LEGACY_VCT } from './crypto.mjs';
import { publicP256 } from './config.mjs';
import { presentationPage } from './ui.mjs';

const walletRequestUri = (clientId, requestUri) => `openid4vp://?${new URLSearchParams({
  client_id: clientId, response_type: 'vp_token', request_uri: requestUri,
  request_uri_method: 'get',
})}`;

export class PresentationError extends Error {
  constructor(reason) {
    super(`Presentation rejected: ${reason}`);
  }
}

export async function presentationService(config, issuerJwk) {
  const publicKey = publicP256({
    kty: issuerJwk.kty, crv: issuerJwk.crv, x: issuerJwk.x, y: issuerJwk.y,
  });
  const requestKid = `did:jwk:${Buffer.from(JSON.stringify(publicKey)).toString('base64url')}#0`;
  const didKey = await EcdsaMultikey.fromJwk({ jwk: publicKey });
  const { publicKeyMultibase } = await didKey.export({ publicKey: true });
  const didKeyDid = `did:key:${publicKeyMultibase}`;
  const didKeyKid = `${didKeyDid}#${publicKeyMultibase}`;
  const didKeyClientId = `decentralized_identifier:${didKeyDid}`;
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
    if (!session) throw new PresentationError('Presentation unavailable or expired');
    return session;
  }
  return {
    create(taskId, contextId) {
      cleanup();
      if (sessions.size >= config.maxEntries) throw new Error('Presentation capacity reached');
      const id = random(), nonce = random(), state = random();
      const responseUri = `${config.origin}/presentations/${id}/response`;
      const clientId = `decentralized_identifier:${requestKid.split('#')[0]}`;
      const session = {
        taskId, contextId, nonce, state, clientId, didKeyClientId,
        expiresAt: config.now() + ttl, verified: false, busy: false,
      };
      const requestObject = clientIdValue => ({
        iss: clientIdValue, aud: 'https://self-issued.me/v2', iat: config.now(), exp: session.expiresAt,
        client_id: clientIdValue, response_type: 'vp_token', response_mode: 'direct_post',
        response_uri: responseUri, nonce, state,
        dcql_query: { credentials: [{
          id: 'age', format: 'dc+sd-jwt', meta: { vct_values: [credentialType(config.origin), LEGACY_VCT] },
          claims: [{ path: ['over_21'], values: [true] }],
        }] },
        client_metadata: {
          client_name: 'AgentCon wine supplier (simulated purchase)',
          vp_formats_supported: { 'dc+sd-jwt': { 'sd-jwt_alg_values': ['ES256'], 'kb-jwt_alg_values': ['ES256'] } },
        },
      });
      const signRequest = (request, kid) => {
        const protectedHeader = { alg: 'ES256', typ: 'oauth-authz-req+jwt', kid };
        const signingInput = [protectedHeader, request]
          .map(value => Buffer.from(JSON.stringify(value)).toString('base64url')).join('.');
        return `${signingInput}.${sign('sha256', Buffer.from(signingInput), {
          key: requestKey, dsaEncoding: 'ieee-p1363',
        }).toString('base64url')}`;
      };
      sessions.set(id, session);
      session.request = signRequest(requestObject(clientId), requestKid);
      session.didKeyRequest = signRequest(requestObject(didKeyClientId), didKeyKid);
      const requestUri = `${config.origin}/presentations/${id}/request`;
      return {
        id, taskId, contextId, expiresAt: session.expiresAt,
        authorizationRequest: { client_id: clientId, request_uri: requestUri },
        presentationUrl: `${config.origin}/presentations/${id}`,
        walletUri: walletRequestUri(clientId, requestUri),
      };
    },
    status(id) {
      const session = get(id);
      return { taskId: session.taskId, contextId: session.contextId, verified: session.verified, expiresAt: session.expiresAt };
    },
    request(id) { return get(id).request; },
    lightSsiRequest(id) { return get(id).didKeyRequest; },
    async page(id) {
      const session = get(id);
      const requestUri = `${config.origin}/presentations/${id}/request`;
      const didKeyRequestUri = `${requestUri}/light-ssi`;
      return presentationPage(walletRequestUri(session.clientId, requestUri), config.origin, session.expiresAt,
        walletRequestUri(session.didKeyClientId, didKeyRequestUri));
    },
    async verify(id, input) {
      const session = get(id);
      if (session.verified || session.busy) throw new PresentationError('Presentation already submitted');
      if (input.state !== session.state) throw new PresentationError('Invalid state');
      let tokens;
      try { tokens = JSON.parse(input.vp_token); } catch { throw new PresentationError('Expected DCQL vp_token object'); }
      if (!tokens || Object.keys(tokens).length !== 1 || !Array.isArray(tokens.age) ||
          tokens.age.length !== 1 || typeof tokens.age[0] !== 'string' || tokens.age[0].length > 12000) {
        throw new PresentationError('Expected one age presentation in DCQL vp_token');
      }
      session.busy = true;
      let stage = 'Invalid issuer JWT header or untrusted issuer key';
      try {
        const presentation = tokens.age[0];
        const [credential] = presentation.split('~');
        const header = decodeProtectedHeader(credential);
        if (header.alg !== 'ES256' || header.typ !== 'dc+sd-jwt' || !trustedKids.includes(header.kid) ||
            header.jwk || header.jku || header.x5u || header.x5c) throw new Error('Untrusted issuer key');
        stage = 'Invalid issuer signature, issuer or credential lifetime';
        const { payload } = await jwtVerify(credential, await importJWK(issuerJwk, 'ES256'), {
          algorithms: ['ES256'], issuer: config.origin, currentDate: new Date(config.now() * 1000),
        });
        stage = 'Invalid issuer credential claims';
        if (!Number.isSafeInteger(payload.exp) || !Number.isSafeInteger(payload.iat) ||
            payload.iat > config.now() || payload.exp <= config.now() ||
            ![credentialType(config.origin), LEGACY_VCT].includes(payload.vct) ||
            payload.demo_only !== true) throw new Error('Invalid issuer claims');
        stage = 'Invalid credential holder public key';
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
        stage = 'Invalid selective disclosures or missing over_21 claim';
        const disclosed = await sd.verify(presentation, { requiredClaimKeys: ['over_21'] });
        if (disclosed.payload.over_21 !== true) throw new Error('Required claim not disclosed');
        stage = 'Missing or invalid holder binding JWT header';
        const segments = presentation.split('~');
        const kb = segments.pop();
        if (!kb) throw new Error('Holder binding required');
        const kbHeader = decodeProtectedHeader(kb);
        if (kbHeader.alg !== 'ES256' || kbHeader.typ !== 'kb+jwt' ||
            kbHeader.jwk || kbHeader.jku || kbHeader.x5u || kbHeader.x5c) throw new Error('Invalid holder header');
        stage = 'Invalid holder signature, audience or JWT lifetime';
        const verified = await jwtVerify(kb, await importJWK(holder, 'ES256'), {
          algorithms: ['ES256'], audience: [session.clientId, session.didKeyClientId],
          currentDate: new Date(config.now() * 1000),
        });
        const claims = verified.payload;
        if (![session.clientId, session.didKeyClientId].includes(claims.aud)) {
          throw new PresentationError('Invalid holder binding audience');
        }
        if (claims.nonce !== session.nonce) throw new PresentationError('Invalid holder binding nonce');
        if (!Number.isSafeInteger(claims.iat) || claims.iat > config.now() + 5 || claims.iat < config.now() - 300) {
          throw new PresentationError('Invalid holder binding issuance time');
        }
        if (claims.sd_hash !== digest(`${segments.join('~')}~`)) {
          throw new PresentationError('Invalid holder binding disclosure hash');
        }
        if (session.expiresAt <= config.now()) throw new PresentationError('Presentation expired');
        session.verified = true;
      } catch (error) {
        throw error instanceof PresentationError ? error : new PresentationError(stage);
      } finally {
        session.busy = false;
      }
    },
  };
}
