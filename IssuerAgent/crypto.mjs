import { createHash, createPrivateKey, randomBytes, sign } from 'node:crypto';
import { calculateJwkThumbprint, decodeProtectedHeader, importJWK, jwtVerify } from 'jose';
import { SDJwtVcInstance } from '@sd-jwt/sd-jwt-vc';
import * as EcdsaMultikey from '@digitalbazaar/ecdsa-multikey';
import { publicP256 } from './config.mjs';

export const LEGACY_VCT = 'urn:agentcon:demo:age-over-21';
export const credentialType = origin => `${origin}/credential-types/age-over-21`;
export const CONFIG_ID = 'AgentConDemoOver21';
export const GRANT = 'urn:ietf:params:oauth:grant-type:pre-authorized_code';
export const random = () => randomBytes(32).toString('base64url');
export const digest = value => createHash('sha256').update(value).digest('base64url');

export class ProofValidationError extends Error {}

export async function signingService(config) {
  const jwk = publicP256({
    kty: config.privateJwk.kty, crv: config.privateJwk.crv,
    x: config.privateJwk.x, y: config.privateJwk.y,
  });
  const kid = await calculateJwkThumbprint(jwk, 'sha256');
  const publicJwk = { ...jwk, kid, alg: 'ES256', use: 'sig' };
  const legacyPublicJwk = {
    ...publicJwk, kid: `did:jwk:${Buffer.from(JSON.stringify(jwk)).toString('base64url')}#0`,
  };
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
    legacyPublicJwk,
    issue: (holder, now) => sd.issue({
      iss: config.origin, iat: now, exp: now + 14 * 24 * 60 * 60,
      vct: credentialType(config.origin), title: 'AgentCon Demo over 21 — NOT age verification',
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
  if (typeof jwt !== 'string' || jwt.length > 8192) throw new ProofValidationError('Expected a compact JWT proof of at most 8192 characters');
  let header;
  try { header = decodeProtectedHeader(jwt); }
  catch { throw new ProofValidationError('Malformed compact JWT proof'); }
  if (header.alg !== 'ES256') throw new ProofValidationError('Unsupported proof algorithm; issuer supports ES256 with P-256 keys');
  if (header.typ !== 'openid4vci-proof+jwt') throw new ProofValidationError('Proof typ must be openid4vci-proof+jwt');
  let jwk;
  try { jwk = await holderKey(header); }
  catch { throw new ProofValidationError('Proof requires one public P-256 jwk or a supported did:jwk/did:key key ID; remote key URLs and private keys are not accepted'); }
  let payload;
  try {
    ({ payload } = await jwtVerify(jwt, await importJWK(jwk, 'ES256'), {
      algorithms: ['ES256'], typ: 'openid4vci-proof+jwt',
      audience: origin, currentDate: new Date(now * 1000), clockTolerance: 5,
    }));
  } catch (error) {
    if (error.code === 'ERR_JWT_CLAIM_VALIDATION_FAILED' && error.claim === 'aud') {
      throw new ProofValidationError('Proof aud must exactly match the credential issuer URL');
    }
    throw new ProofValidationError('Proof signature or JWT time validation failed');
  }
  if (payload.aud !== origin) throw new ProofValidationError('Proof aud must be a single credential issuer URL');
  if (!Number.isSafeInteger(payload.iat) || payload.iat > now + 5 || payload.iat < now - 300 ||
      (payload.exp !== undefined && (!Number.isSafeInteger(payload.exp) || payload.exp <= now)) ||
      (payload.nbf !== undefined && (!Number.isSafeInteger(payload.nbf) || payload.nbf > now + 5))) {
    throw new ProofValidationError('Proof timestamps must be integer seconds and within the issuer freshness window');
  }
  if (typeof payload.nonce !== 'string' || !/^[A-Za-z0-9_-]{43}$/.test(payload.nonce)) {
    throw new ProofValidationError('Proof nonce must be the c_nonce supplied by the issuer');
  }
  if (payload.iss !== undefined && (typeof payload.iss !== 'string' || !clientId || payload.iss !== clientId)) {
    throw new ProofValidationError(clientId
      ? 'Proof iss must match the client_id used at token redemption'
      : 'Proof iss must be omitted for anonymous pre-authorized issuance');
  }
  return { jwk, nonce: payload.nonce };
}
