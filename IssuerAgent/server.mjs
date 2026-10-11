import { createServer } from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import { CONFIG_ID, GRANT, VCT, digest, random, signingService, validateProof } from './crypto.mjs';
import { isMain, loadConfig } from './config.mjs';
import { offerPage, rootPage } from './ui.mjs';

const MAX_BODY = 16 * 1024;
class HttpError extends Error {
  constructor(status, error, description = error, extra = {}) {
    super(description);
    Object.assign(this, { status, error, extra });
  }
}
const fail = (status, error, description, extra) => { throw new HttpError(status, error, description, extra); };
const bearer = req => /^Bearer ([A-Za-z0-9_-]{32,256})$/i.exec(req.headers.authorization ?? '')?.[1];
const equal = (a, b) => timingSafeEqual(Buffer.from(digest(a)), Buffer.from(digest(b)));
function fields(body, allowed) {
  if (!body || typeof body !== 'object' || Array.isArray(body) ||
      Object.keys(body).some(key => !allowed.includes(key))) fail(400, 'invalid_request', 'Unsupported request fields');
}
async function body(req, type) {
  const contentType = (req.headers['content-type'] ?? '').split(';')[0].trim().toLowerCase();
  if (contentType !== type) fail(415, 'invalid_request', `Expected ${type}`);
  if (req.headers['content-encoding'] && req.headers['content-encoding'] !== 'identity') {
    fail(415, 'invalid_request', 'Content encoding is not supported');
  }
  if (Number(req.headers['content-length']) > MAX_BODY) fail(413, 'invalid_request', 'Request body too large');
  let size = 0;
  const chunks = [];
  for await (const chunk of req.iterator({ destroyOnReturn: false })) {
    size += chunk.length;
    if (size > MAX_BODY) fail(413, 'invalid_request', 'Request body too large');
    chunks.push(chunk);
  }
  const text = Buffer.concat(chunks).toString('utf8');
  if (type === 'application/json') {
    try { return JSON.parse(text); } catch { fail(400, 'invalid_request', 'Invalid JSON'); }
  }
  const params = new URLSearchParams(text);
  const result = {};
  for (const [key, value] of params) {
    if (Object.hasOwn(result, key)) fail(400, 'invalid_request', 'Duplicate form parameter');
    Object.defineProperty(result, key, { value, enumerable: true });
  }
  return result;
}

export async function createIssuer(config) {
  const signer = await signingService(config);
  const offers = new Map(), codes = new Map(), tokens = new Map(), nonces = new Map(), rates = new Map();
  const stores = [offers, codes, tokens, nonces, rates];
  function cleanup() {
    const now = config.now();
    for (const store of stores) {
      for (const [key, value] of store) if (value.expiresAt <= now) store.delete(key);
    }
  }
  function capacity(...maps) {
    if (maps.some(map => map.size >= config.maxEntries)) fail(503, 'temporarily_unavailable', 'Demo capacity reached; retry later');
  }
  function nonce(owner) {
    capacity(nonces);
    const value = random();
    nonces.set(digest(value), { owner, expiresAt: config.now() + config.nonceTtl });
    return value;
  }
  function tokenNonce(token, tokenId) {
    if (token.nonceId) nonces.delete(token.nonceId);
    const value = nonce(tokenId);
    token.nonceId = digest(value);
    return { c_nonce: value, c_nonce_expires_in: config.nonceTtl };
  }
  function activeToken(req) {
    const value = bearer(req);
    const id = value && digest(value);
    const token = id && tokens.get(id);
    if (!token || token.expiresAt <= config.now() || token.used) fail(401, 'invalid_token', 'Invalid or expired access token');
    return { token, id };
  }
  const issuerMetadata = {
    credential_issuer: config.origin,
    authorization_servers: [config.origin],
    credential_endpoint: `${config.origin}/credential`,
    nonce_endpoint: `${config.origin}/nonce`,
    display: [{ name: 'AgentCon DEMO ONLY issuer — no real age verification', locale: 'en-US' }],
    credential_configurations_supported: {
      [CONFIG_ID]: {
        format: 'dc+sd-jwt', vct: VCT, scope: 'agentcon_demo_over21',
        cryptographic_binding_methods_supported: ['jwk', 'did:jwk', 'did:key'],
        credential_signing_alg_values_supported: ['ES256'],
        proof_types_supported: { jwt: { proof_signing_alg_values_supported: ['ES256'] } },
        display: [{ name: 'AgentCon DEMO over 21 — NOT age verification', locale: 'en-US' }],
        claims: [
          { path: ['over_21'], mandatory: true, display: [{ name: 'DEMO over 21', locale: 'en-US' }] },
          { path: ['demo_only'], mandatory: true, display: [{ name: 'DEMO ONLY', locale: 'en-US' }] },
        ],
      },
    },
  };
  const authMetadata = {
    issuer: config.origin, token_endpoint: `${config.origin}/token`,
    token_endpoint_auth_methods_supported: ['none'],
    grant_types_supported: [GRANT], response_types_supported: [],
    scopes_supported: ['agentcon_demo_over21'],
    'pre-authorized_grant_anonymous_access_supported': true,
  };
  const server = createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Pragma', 'no-cache');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");
    res.setHeader('Strict-Transport-Security', 'max-age=31536000');
    function send(status, value, html = false) {
      res.writeHead(status, { 'Content-Type': html ? 'text/html; charset=utf-8' : 'application/json; charset=utf-8' });
      res.end(html ? value : JSON.stringify(value));
    }
    try {
      cleanup();
      const path = new URL(req.url, 'http://internal.invalid').pathname;
      if (req.method === 'GET' && path === '/health') return send(200, { status: 'ok', demo_only: true });
      // Do not trust caller-supplied forwarding headers. App Service must terminate TLS and enforce HTTPS Only.
      const ip = req.socket.remoteAddress ?? 'unknown';
      let rate = rates.get(ip);
      if (!rate) {
        capacity(rates);
        rate = { count: 0, expiresAt: config.now() + config.rateWindow };
        rates.set(ip, rate);
      }
      if (++rate.count > config.rateLimit) {
        res.setHeader('Retry-After', String(Math.max(1, rate.expiresAt - config.now())));
        fail(429, 'slow_down', 'Request rate exceeded');
      }
      const methods = {
        '/': 'GET', '/health': 'GET', '/.well-known/openid-credential-issuer': 'GET',
        '/.well-known/oauth-authorization-server': 'GET', '/.well-known/openid-configuration': 'GET',
        '/.well-known/jwt-vc-issuer': 'GET', '/jwks': 'GET', '/admin/offers': 'POST',
        '/token': 'POST', '/nonce': 'POST', '/credential': 'POST',
      };
      const offerMatch = /^\/(offers|credential-offer)\/([A-Za-z0-9_-]{43})$/.exec(path);
      const method = offerMatch ? 'GET' : methods[path];
      if (!method) fail(404, 'not_found');
      if (req.method !== method) {
        res.setHeader('Allow', method);
        fail(405, 'invalid_request', 'Method not allowed');
      }
      if (path === '/') return send(200, rootPage(), true);
      if (path === '/.well-known/openid-credential-issuer') return send(200, issuerMetadata);
      if (path === '/.well-known/oauth-authorization-server' || path === '/.well-known/openid-configuration') {
        return send(200, authMetadata);
      }
      if (path === '/.well-known/jwt-vc-issuer') return send(200, { issuer: config.origin, jwks_uri: `${config.origin}/jwks` });
      if (path === '/jwks') return send(200, { keys: [signer.publicJwk] });
      if (path === '/admin/offers') {
        const auth = bearer(req);
        if (!auth || !equal(auth, config.adminToken)) fail(401, 'invalid_token', 'Administrator authorization required');
        const input = await body(req, 'application/json');
        fields(input, []);
        capacity(offers, codes);
        const id = random(), code = random(), expiresAt = config.now() + config.offerTtl;
        const offer = {
          credential_issuer: config.origin, credential_configuration_ids: [CONFIG_ID],
          grants: { [GRANT]: { 'pre-authorized_code': code } },
        };
        offers.set(id, { expiresAt, offer });
        codes.set(digest(code), { expiresAt, offerId: id });
        const offerUri = `${config.origin}/credential-offer/${id}`;
        return send(201, {
          offer_id: id, expires_at: new Date(expiresAt * 1000).toISOString(),
          offer_page: `${config.origin}/offers/${id}`, credential_offer_uri: offerUri,
          wallet_uri: `openid-credential-offer://?credential_offer_uri=${encodeURIComponent(offerUri)}`,
          demo_only: true,
        });
      }
      if (offerMatch) {
        const entry = offers.get(offerMatch[2]);
        if (!entry) fail(404, 'not_found', 'Offer unavailable or expired');
        if (offerMatch[1] === 'credential-offer') return send(200, entry.offer);
        const uri = `${config.origin}/credential-offer/${offerMatch[2]}`;
        return send(200, await offerPage(`openid-credential-offer://?credential_offer_uri=${encodeURIComponent(uri)}`, entry.expiresAt), true);
      }
      if (path === '/token') {
        const input = await body(req, 'application/x-www-form-urlencoded');
        fields(input, ['grant_type', 'pre-authorized_code', 'client_id', 'scope']);
        if (!input.grant_type) fail(400, 'invalid_request', 'grant_type required');
        if (input.grant_type !== GRANT) fail(400, 'unsupported_grant_type');
        if (!/^[A-Za-z0-9_-]{43}$/.test(input['pre-authorized_code'] ?? '')) fail(400, 'invalid_grant');
        if (input.client_id !== undefined && (!input.client_id || input.client_id.length > 2048)) fail(400, 'invalid_request', 'Invalid client_id');
        if (input.scope !== undefined && input.scope !== 'agentcon_demo_over21') fail(400, 'invalid_scope');
        const codeId = digest(input['pre-authorized_code']);
        const code = codes.get(codeId);
        if (!code || code.expiresAt <= config.now()) fail(400, 'invalid_grant', 'Invalid, redeemed or expired pre-authorized code');
        capacity(tokens, nonces);
        const accessToken = random(), tokenId = digest(accessToken);
        const token = { expiresAt: config.now() + config.tokenTtl, clientId: input.client_id, used: false, busy: false };
        const challenge = tokenNonce(token, tokenId);
        // No await between code lookup and redemption: exactly one concurrent request can succeed.
        codes.delete(codeId);
        offers.delete(code.offerId);
        tokens.set(tokenId, token);
        return send(200, {
          access_token: accessToken, token_type: 'Bearer', expires_in: config.tokenTtl,
          scope: 'agentcon_demo_over21', ...challenge,
        });
      }
      if (path === '/nonce') {
        // Sphereon sends an empty form; final wallets may send no content type/body.
        if (req.headers['content-type']) {
          const type = req.headers['content-type'].split(';')[0].trim().toLowerCase();
          if (!['application/json', 'application/x-www-form-urlencoded'].includes(type)) fail(415, 'invalid_request');
          fields(await body(req, type), []);
        } else {
          let size = 0;
          for await (const chunk of req.iterator({ destroyOnReturn: false })) {
            size += chunk.length;
            if (size > 0) fail(400, 'invalid_request', 'Nonce request must be empty');
          }
        }
        if (req.headers.authorization !== undefined) {
          const { token, id } = activeToken(req);
          if (token.busy) fail(409, 'invalid_request', 'Issuance already in progress');
          return send(200, tokenNonce(token, id));
        }
        return send(200, { c_nonce: nonce(), c_nonce_expires_in: config.nonceTtl });
      }
      if (path === '/credential') {
        const { token, id } = activeToken(req);
        if (token.busy) fail(409, 'invalid_request', 'Issuance already in progress');
        token.busy = true;
        try {
          const input = await body(req, 'application/json');
          fields(input, ['credential_configuration_id', 'proof', 'proofs']);
          if (input.credential_configuration_id !== CONFIG_ID) fail(400, 'unknown_credential_configuration');
          if (!!input.proof === !!input.proofs) fail(400, 'invalid_proof', 'Exactly one proof is required');
          let jwt;
          if (input.proof) {
            fields(input.proof, ['proof_type', 'jwt']);
            if (input.proof.proof_type !== 'jwt') fail(400, 'invalid_proof');
            jwt = input.proof.jwt;
          } else {
            fields(input.proofs, ['jwt']);
            if (!Array.isArray(input.proofs.jwt) || input.proofs.jwt.length !== 1) fail(400, 'invalid_proof', 'Exactly one JWT proof is supported');
            [jwt] = input.proofs.jwt;
          }
          let verified;
          try {
            verified = await validateProof(jwt, config.origin, config.now(), token.clientId);
          } catch {
            fail(400, 'invalid_proof', 'Proof signature, key reference or claims invalid');
          }
          // Recheck expiration after asynchronous verification and before consuming capabilities.
          if (token.expiresAt <= config.now()) fail(401, 'invalid_token', 'Expired access token');
          const nonceId = digest(verified.nonce), challenge = nonces.get(nonceId);
          if (!challenge || challenge.expiresAt <= config.now() ||
              (challenge.owner !== undefined && challenge.owner !== id)) {
            fail(400, 'invalid_nonce', 'Missing, expired, consumed or incorrect nonce');
          }
          nonces.delete(nonceId);
          if (token.nonceId) nonces.delete(token.nonceId);
          token.used = true;
          const credential = await signer.issue(verified.jwk, config.now());
          return send(200, { credentials: [{ credential }] });
        } catch (error) {
          if (error instanceof HttpError && ['invalid_proof', 'invalid_nonce'].includes(error.error)) {
            // Retry requires a freshly signed proof, never acceptance of the failed proof.
            Object.assign(error.extra, tokenNonce(token, id));
          }
          throw error;
        } finally {
          token.busy = false;
        }
      }
    } catch (error) {
      if (res.destroyed || res.headersSent) return;
      const known = error instanceof HttpError;
      if (!req.complete) {
        res.setHeader('Connection', 'close');
        req.resume();
      }
      if (known && error.status === 401) res.setHeader('WWW-Authenticate', 'Bearer error="invalid_token"');
      send(known ? error.status : 500, {
        error: known ? error.error : 'server_error',
        error_description: known ? error.message : 'Demo issuer request failed',
        ...(known ? error.extra : {}),
      });
      // Never log request URLs, headers, bodies, tokens, proof JWTs, credentials or private keys.
      if (!known) console.error('Demo issuer request failed (details suppressed)');
    }
  });
  server.requestTimeout = 10_000;
  server.headersTimeout = 10_000;
  server.keepAliveTimeout = 5000;
  const timer = setInterval(cleanup, 30_000);
  timer.unref();
  server.on('close', () => clearInterval(timer));
  return server;
}

if (isMain(import.meta.url)) {
  try {
    const config = await loadConfig();
    const server = await createIssuer(config);
    server.on('error', () => {
      console.error('Demo issuer could not listen');
      process.exitCode = 1;
    });
    server.listen(config.port, '0.0.0.0', () => console.log(`AgentCon DEMO issuer listening on port ${config.port}`));
    const shutdown = () => server.close(() => process.exit(0));
    process.once('SIGTERM', shutdown);
    process.once('SIGINT', shutdown);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
