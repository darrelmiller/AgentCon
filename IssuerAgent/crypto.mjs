import { createHash, createPrivateKey, randomBytes, sign } from 'node:crypto';
import { decodeProtectedHeader, importJWK, jwtVerify } from 'jose';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';
import * as EcdsaMultikey from '@digitalbazaar/ecdsa-multikey';
import { publicP256 } from './config.mjs';

export const VCT = 'urn:agentcon:demo:age-over-21';
export const CONFIG_ID = 'AgentConDemoOver21';
export const GRANT = 'urn:ietf:params:oauth:grant-type:pre-authorized_code';
export const random = () => randomBytes(32).toString('base64url');
export const digest = value => createHash('sha256').update(value).digest('base64url');

export async function signingService(config) {
  const jwk = publicP256({
    kty: config.privateJwk.kty, crv: config.privateJwk.crv,
    x: config.privateJwk.x, y: config.privateJwk.y,
  });
  // The pinned Sphereon holder resolves a DID kid, not HTTPS JWKS, during SD-JWT verification.
  // Standards-based verifiers can select this identical opaque kid from the HTTPS issuer JWKS.
  const kid = `did:jwk:${Buffer.from(JSON.stringify(jwk)).toString('base64url')}#0`;
  const publicJwk = { ...jwk, kid, alg: 'ES256', use: 'sig' };
  const key = createPrivateKey({ key: config.privateJwk, format: 'jwk' });
  const sd = new SDJwtVcInstance({
    signAlg: 'ES256',
    hashAlg: 'sha-256',
    hasher: data => createHash('sha256').update(typeof data === 'string' ? data : Buffer.from(data)).digest(),
    saltGenerator: length => randomBytes(length).toString('base64url'),
    signer: data => sign('sha256', Buffer.from(data), {
      key, dsaEncoding: 'ieee-p1363',
    }).toString('base64url'),
  });
  return {
    publicJwk,
    issue: (holder, now) => sd.issue({
      iss: config.origin, iat: now, exp: now + 14 * 24 * 60 * 60,
      vct: VCT, title: 'AgentCon Demo over 21 — NOT age verification',
      demo_only: true, cnf: { jwk: holder }, over_21: true,
    }, { _sd: ['over_21'] }, { header: { typ: 'dc+sd-jwt', kid } }),
  };
}

async function holderKey(header) {
  if (header.jku !== undefined || header.x5u !== undefined || header.x5c !== undefined ||
      (!!header.jwk === !!header.kid)) throw new Error('Unsupported key reference');
  if (header.jwk) return publicP256(header.jwk);
  if (typeof header.kid !== 'string' || header.kid.length > 2048) throw new Error('Invalid kid');
  const [did, fragment, extra] = header.kid.split('#');
  if (extra !== undefined) throw new Error('Invalid DID fragment');
  if (did.startsWith('did:jwk:')) {
    if (fragment !== '0') throw new Error('Invalid did:jwk fragment');
    const encoded = did.slice(8);
    if (!/^[A-Za-z0-9_-]+$/.test(encoded)) throw new Error('Invalid did:jwk');
    const bytes = Buffer.from(encoded, 'base64url');
    if (bytes.toString('base64url') !== encoded) throw new Error('Noncanonical did:jwk');
    return publicP256(JSON.parse(bytes.toString('utf8')));
  }
  if (did.startsWith('did:key:')) {
    const fingerprint = did.slice(8);
    if (fragment !== fingerprint || !/^z[1-9A-HJ-NP-Za-km-z]+$/.test(fingerprint)) {
      throw new Error('Invalid did:key fragment');
    }
    // Multicodec decoding and elliptic-curve point decompression are library operations.
    const pair = await EcdsaMultikey.from({ publicKeyMultibase: fingerprint });
    const jwk = await EcdsaMultikey.toJwk({ keyPair: pair });
    return publicP256({ kty: jwk.kty, crv: jwk.crv, x: jwk.x, y: jwk.y });
  }
  throw new Error('Only inline jwk, did:jwk and P-256 did:key are supported');
}

export async function validateProof(jwt, origin, now, clientId) {
  if (typeof jwt !== 'string' || jwt.length > 8192) throw new Error('Invalid proof');
  const header = decodeProtectedHeader(jwt);
  if (header.alg !== 'ES256' || header.typ !== 'openid4vci-proof+jwt') throw new Error('Invalid proof header');
  const jwk = await holderKey(header);
  const { payload } = await jwtVerify(jwt, await importJWK(jwk, 'ES256'), {
    algorithms: ['ES256'], typ: 'openid4vci-proof+jwt',
    audience: origin, currentDate: new Date(now * 1000), clockTolerance: 5,
  });
  if (payload.aud !== origin || !Number.isSafeInteger(payload.iat) ||
      payload.iat > now + 5 || payload.iat < now - 300 ||
      (payload.exp !== undefined && (!Number.isSafeInteger(payload.exp) || payload.exp <= now)) ||
      (payload.nbf !== undefined && (!Number.isSafeInteger(payload.nbf) || payload.nbf > now + 5)) ||
      typeof payload.nonce !== 'string' || !/^[A-Za-z0-9_-]{43}$/.test(payload.nonce) ||
      (payload.iss !== undefined && (typeof payload.iss !== 'string' ||
        !clientId || payload.iss !== clientId))) throw new Error('Invalid proof claims');
  return { jwk, nonce: payload.nonce };
}
