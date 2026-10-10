import { readFile, realpath } from 'node:fs/promises';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPrivateKey, createPublicKey } from 'node:crypto';

export function publicP256(jwk) {
  if (!jwk || typeof jwk !== 'object' || Array.isArray(jwk) ||
      jwk.kty !== 'EC' || jwk.crv !== 'P-256' || jwk.d !== undefined ||
      !/^[A-Za-z0-9_-]{43}$/.test(jwk.x ?? '') ||
      !/^[A-Za-z0-9_-]{43}$/.test(jwk.y ?? '') ||
      (jwk.alg !== undefined && jwk.alg !== 'ES256') ||
      (jwk.use !== undefined && jwk.use !== 'sig') ||
      (jwk.key_ops !== undefined && (!Array.isArray(jwk.key_ops) ||
        jwk.key_ops.some(op => op !== 'verify')))) {
    throw new Error('Expected a public ES256 P-256 JWK');
  }
  const key = { kty: 'EC', crv: 'P-256', x: jwk.x, y: jwk.y };
  createPublicKey({ key, format: 'jwk' });
  return key;
}

export async function loadConfig(env = process.env) {
  let origin;
  try {
    const url = new URL(env.ISSUER_BASE_URL);
    if (url.protocol !== 'https:' || url.username || url.password ||
        url.pathname !== '/' || url.search || url.hash) throw new Error();
    origin = url.origin;
  } catch {
    throw new Error('ISSUER_BASE_URL must be an HTTPS origin without path, credentials, query or fragment');
  }
  if (typeof env.ISSUER_ADMIN_TOKEN !== 'string' ||
      !/^[A-Za-z0-9_-]{32,256}$/.test(env.ISSUER_ADMIN_TOKEN)) {
    throw new Error('ISSUER_ADMIN_TOKEN must be 32–256 base64url characters');
  }
  if (!!env.ISSUER_PRIVATE_JWK === !!env.ISSUER_PRIVATE_JWK_PATH) {
    throw new Error('Set exactly one of ISSUER_PRIVATE_JWK or ISSUER_PRIVATE_JWK_PATH');
  }
  const port = Number(env.PORT ?? 3000);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('PORT must be 1–65535');
  let privateJwk;
  try {
    let source = env.ISSUER_PRIVATE_JWK;
    if (!source) {
      if (!isAbsolute(env.ISSUER_PRIVATE_JWK_PATH)) throw new Error();
      const path = await realpath(env.ISSUER_PRIVATE_JWK_PATH);
      const repo = fileURLToPath(new URL('../', import.meta.url));
      const rel = relative(repo, path);
      if (!rel || (!isAbsolute(rel) && rel !== '..' && !rel.startsWith(`..${sep}`))) throw new Error();
      source = await readFile(path, 'utf8');
    }
    if (Buffer.byteLength(source) > 8192) throw new Error();
    privateJwk = JSON.parse(source);
    if (!/^[A-Za-z0-9_-]{43}$/.test(privateJwk.d ?? '')) throw new Error();
    const { d, ...publicPart } = privateJwk;
    // Private key_ops may legitimately be ["sign"]; public material is validated separately.
    delete publicPart.key_ops;
    publicP256(publicPart);
    const key = createPrivateKey({ key: privateJwk, format: 'jwk' });
    const derived = createPublicKey(key).export({ format: 'jwk' });
    if (derived.x !== privateJwk.x || derived.y !== privateJwk.y) throw new Error();
    // Test the pair at startup, not on the first phone request.
    const { sign, verify } = await import('node:crypto');
    const data = Buffer.from('AgentCon DEMO issuer startup key check');
    if (!verify('sha256', data, createPublicKey(key), sign('sha256', data, key))) throw new Error();
  } catch {
    throw new Error('Issuer key must be a valid private ES256 P-256 JWK; key files must be outside the repository');
  }
  return {
    origin, port, adminToken: env.ISSUER_ADMIN_TOKEN, privateJwk,
    offerTtl: 600, tokenTtl: 600, nonceTtl: 300,
    maxEntries: 1000, rateLimit: 120, rateWindow: 60,
    now: () => Math.floor(Date.now() / 1000),
  };
}

export function isMain(metaUrl) {
  return process.argv[1] && fileURLToPath(metaUrl) === resolve(process.argv[1]);
}
