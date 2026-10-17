# AgentCon DEMO credential issuer

**DEMO ONLY. No identity or age validation occurs.** Every administrator-authorized
test issuance sets `over_21: true`. Do not use this issuer or its credentials to
grant access to age-restricted services.

Self-contained Node.js 22/24 HTTP service for OID4VCI pre-authorized issuance to
compatible SD-JWT VC wallets. It signs a **new credential bound to the wallet's proof
key**, not the software holder used by `tools\credentials`. No LLM, global
credential package, database, automatic key generation, or remote DID resolution.

## Credential and protocol

- Configuration ID: `AgentConDemoOver21`.
- Format and signed JWT `typ`: **`dc+sd-jwt`**, ES256.
- `vct`: `<ISSUER_BASE_URL>/credential-types/age-over-21`, an HTTPS type-metadata URL.
- Visible: `demo_only: true`, demo title, issuer, issuance/expiry timestamps,
  `cnf.jwk` (wallet's verified public P-256 key).
- Selectively disclosable: `over_21: true`; delivered with its disclosure.
- Expiry: 14 days. No credential status/revocation service is advertised.
- `iss` is the configured **public HTTPS origin**. Public key discovery is
  `/.well-known/jwt-vc-issuer` → `/jwks`; no private material is returned.
  New credentials use an RFC 7638 SHA-256 JWK thumbprint as `kid`, selected from
  that JWKS. The original DID key ID remains published for existing credentials;
  no signing-key rotation or reissuance is required.

### Wallet-neutral profile

The issuer no longer depends on Sphereon's SDK/parser or its DID-only
credential-signature discovery. HTTPS type metadata describes the demo type,
display text and disclosure policy. Legacy single-proof request shapes and DID
holder proofs remain accepted as additive protocol support, not vendor branches.
The signing key, holder binding, replay checks and demo-only semantics are unchanged.
Older wallets requiring DID-only issuer-key discovery may no longer import new
credentials. Compatibility with Light SSI Wallet, Lissi ID-Wallet and Hopae EUDI
Wallet requires real-device validation; trust policies may impose further limits.

### Historical Sphereon profile (superseded)

The following explains the original implementation, not the current key/type
discovery or presentation profile. The vendor dependency and pinned parser test
have been removed in favor of vendor-independent protocol tests.

The actual [mobile-wallet 0.8.0 package manifest at
f3b9fa76fe451fa687d21dafd464307397a9f35d](https://github.com/Sphereon-Opensource/mobile-wallet/blob/f3b9fa76fe451fa687d21dafd464307397a9f35d/package.json)
pins `@sphereon/oid4vci-client` **0.20.1** and `@sd-jwt/sd-jwt-vc` **0.15.1**.
The authoritative pinned client source was inspected, not inferred from
final-only examples:

- [Draft-15 client](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/client/lib/OpenID4VCIClientV1_0_15.ts)
  caches token-response `c_nonce` and obtains `/nonce` when needed.
- [Credential request builder](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/client/lib/CredentialRequestClient.ts)
  sends `credential_configuration_id` and a single `proof` object, without
  `format`/`vct` in the request.
- [Proof builder](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/client/lib/ProofOfPossessionBuilder.ts)
  sets `typ: openid4vci-proof+jwt`, issuer audience, nonce, and `jwk` or `kid`.
  Its [proof utility](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/oid4vci-common/lib/functions/ProofUtil.ts)
  uses integer `iat = now - 60s` and `exp = now + 600s`, which this issuer accepts.
- [SD-JWT end-to-end tests](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/client/lib/__tests__/SdJwt.spec.ts)
  explicitly advertise **`dc+sd-jwt`** and expect
  `{"credentials":[{"credential":"..."}]}`.
- [Wallet SD-JWT library 0.15.1](https://github.com/openwallet-foundation/sd-jwt-js/blob/v0.15.1/packages/sd-jwt-vc/src/sd-jwt-vc-instance.ts)
  itself sets `typ: dc+sd-jwt`.
- [Draft-15 types](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/oid4vci-common/lib/types/v1_0_15.types.ts)
  describe configuration IDs, claim-path metadata and credential arrays.
- [Nonce client](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/client/lib/NonceClient.ts)
  uses an empty form POST. [Token client](https://github.com/Sphereon-Opensource/OID4VCI/blob/v0.20.1/packages/client/lib/AccessTokenClient.ts)
  uses form-encoded pre-authorized-code redemption.
- [Wallet holder SDK 0.37.0](https://github.com/Sphereon-Opensource/SSI-SDK/blob/v0.37.0/packages/oid4vci-holder/src/services/OID4VCIHolderService.ts)
  extracts `credentials[0].credential`, supports P-256 holder keys and skips
  remote type-metadata loading for non-HTTP `vct` values (such as this demo URN).
- [Pinned SD-JWT signature verifier](https://github.com/Sphereon-Opensource/SSI-SDK/blob/v0.37.0/packages/sd-jwt/src/action-handler.ts)
  (`verifyCallbackImpl`) resolves a DID in `header.kid`, but has no HTTPS
  issuer-JWKS discovery branch. Therefore the original signed header used `did:jwk:...#0`
  rather than an HTTPS key ID. This is a public, self-describing key ID, **not**
  a different credential issuer. `iss` remains HTTPS and the same key ID/key
  appear in HTTPS JWKS for standards-based issuer discovery. No untrusted
  inline signing-key override is needed.

The issuer advertises one `dc+sd-jwt` configuration, not conflicting format
aliases or draft-11 request fields. Although the pinned types also allow
`vc+sd-jwt`, it is deliberately not advertised: `dc+sd-jwt` has explicit pinned
client test evidence in the original profile. The server additionally accepts final-style
`proofs: {"jwt": ["<compact JWT>"]}` (exactly one JWT) and a public nonce endpoint.
This is a narrow draft-15/final-compatible subset, **not a claim of full final
conformance**. Batch issuance, authorization-code flow, client authentication,
DPoP, attestation, response encryption, deferred issuance and notifications are
not advertised or implemented.

### Browser wallets and CORS

Public discovery, JWKS, type metadata, raw credential offers, token, nonce,
credential and presentation request/response endpoints return
`Access-Control-Allow-Origin: *`, including error responses. OPTIONS preflights
allow only each endpoint's GET or POST method and `Authorization`, `Content-Type`
and `Accept` headers. Browser cookies/credentialed CORS are not enabled.
Issuance still requires a one-use offer, access token and wallet-key proof.
The `/admin/*` APIs have **no CORS grant** and still require the admin token.
HTML pages are not cross-origin APIs. CORS does not override wallet trust policy.

Anonymous pre-authorized redemption is supported; a `client_id` is optional.
Proof `iss` must be omitted for anonymous pre-authorized redemption. With a
`client_id`, any proof `iss` must match it. Proofs require ES256, the correct `typ`,
exact issuer audience, `iat` within five minutes (five-second forward skew),
valid optional `exp`/`nbf`, and a live one-use nonce. Embedded private keys,
unsupported curves, unsafe JOSE key URLs/certificates, unknown DID methods,
bad signatures and replay are rejected. Self-describing `did:jwk#0` is imported
with JOSE/Node key validation; P-256 `did:key` multicodec/point decoding uses
`@digitalbazaar/ecdsa-multikey`. Neither path makes network requests.
Proof errors return fixed diagnostic descriptions for malformed JWTs, unsupported
algorithms/keys, audience, timestamps, nonce shape and client binding. These never
include submitted JWTs, keys, DIDs, claim values or credentials. Invalid proofs
still rotate the nonce and cannot issue a credential.
Some wallets send both a public `jwk` and a `kid` in the proof header even though
the OID4VCI profile specifies them as alternatives. When a valid public JWK is
present, the issuer uses that signed key and treats a bounded `kid` as an
unresolved identifier; it never fetches or resolves it. Proof signatures are
verified with the public JWK and the resulting credential is bound to that same
key. Private JWK material, remote key URLs and certificate references remain
rejected. When no public JWK is present, only locally decoded `did:jwk` and
P-256 `did:key` are accepted.

## Run locally

Use PowerShell and Node 22 or 24:

```powershell
Set-Location .\IssuerAgent
npm ci --no-fund --no-audit

# Use an existing persistent private ES256 JWK OUTSIDE the repository.
$env:ISSUER_PRIVATE_JWK_PATH = 'C:\secure-state\issuer-private.jwk.json'
$env:ISSUER_BASE_URL = 'https://agentcon-issuer.azurewebsites.net'
# Supply a secret from your secret manager; do not paste it into scripts or Git.
$env:ISSUER_ADMIN_TOKEN = '<32-or-more-random-base64url-characters>'
$env:PORT = '3000'
npm start
```

The admin-token placeholder above is not valid configuration. Use a generated
32-byte base64url secret, not a password. An HTTPS tunnel/reverse proxy is required
for a phone to reach a local service; configure its exact HTTPS origin. Node
listens on HTTP internally, as required by App Service TLS termination. Do not
expose this listener directly to the internet. `ISSUER_BASE_URL` never comes from
Host or forwarding headers and rejects HTTP, paths, credentials, queries and
fragments.

Alternatively set `ISSUER_PRIVATE_JWK` to the private JWK JSON through a secret
manager or protected environment setting. Set **exactly one** key variable.
Startup fails on missing/invalid keys and never silently regenerates them.
Key files must have an absolute path outside this repository; symlinks are
resolved before checking. Protect the file with OS permissions.

Create an offer from an authorized terminal:

```powershell
$env:ISSUER_BASE_URL = 'https://agentcon-issuer.azurewebsites.net'
# Set ISSUER_ADMIN_TOKEN through your protected secret source first.
node .\create-offer.mjs
```

The local client needs only `ISSUER_BASE_URL` and `ISSUER_ADMIN_TOKEN`; it never
loads the signing key. It sends the token in an Authorization header, refuses
redirects, times out after 15 seconds, and prints **only the private offer page
URL** on success. Open that URL to see the embedded QR. `npm run offer` invokes
the same command (npm also prints its script banner); use `node` directly when
capturing URL-only stdout. Errors contain no response bodies or secrets.

Do not share the admin token. The returned page, offer URI and wallet link are
**capabilities**: anyone holding one can claim that test issuance. The page
contains a server-generated, embedded PNG QR and wallet link:

```text
openid-credential-offer://?credential_offer_uri=<percent-encoded-HTTPS-offer-URL>
```

After a wallet attempt, refresh the private offer page to see the last issuer-side
stage: offer fetched, token redeemed, credential request rejected (with a fixed,
non-sensitive reason), or credential issued. The page remains available until
the original offer expiry even after redemption, but no longer shows a QR or
wallet link. Raw offer JSON still becomes unavailable immediately on redemption.
Each bounded progress record contains only expiry, stage and a fixed diagnostic;
no wallet identifiers, keys, JWTs, tokens or credentials are stored in it.
Successful issuer response does not establish that the wallet stored the credential.
Use a separate offer for every wallet test. An unchanged stage means that the
issuer has not observed the next protocol step; wallet-local errors are not visible.

Scan with a compatible wallet on the phone, accept issuance, and confirm it is shown
as a DEMO credential. **No phone roundtrip has been verified by the automated
tests; actual mobile acceptance is only established after a successful scan.**
Generating a pre-bound software credential and importing its string is not a
substitute for this flow.

## Endpoint contract

### OID4VP purchase presentations

The application also hosts a bounded in-memory OID4VP 1.0 verifier using its existing
issuer public key as the trust anchor. It does not accept arbitrary DID issuers or
fetch caller-provided key URLs. The supplier creates a 10-minute challenge with its
task/context IDs through `POST /admin/presentations` (admin bearer token; JSON
`{taskId, contextId}`), then receives the experimental A2A authorization request,
presentation page URL, wallet URI and expiry.

`GET /presentations/<id>` renders a local QR for a compatible wallet. The QR invokes
`openid4vp://` with `client_id` and an HTTPS `request_uri`.
`GET /presentations/<id>/request` returns an ES256-signed JWT request object
(`application/oauth-authz-req+jwt`, `oauth-authz-req+jwt` protected-header type,
`decentralized_identifier:did:jwk:...` client identifier, DID key ID and
`direct_post`). This is the final OpenID4VP decentralized-identifier prefix,
not the older unprefixed DID client ID. The verifier signs with
the existing issuer key; the DID resolves that public key without a network key
lookup. The signed request includes issuer, wallet audience, issue time and expiry.
DCQL requests only the `over_21:true` disclosure from the HTTPS credential type
or the original `urn:agentcon:demo:age-over-21`, format `dc+sd-jwt`, with ES256
credential and key-binding signatures. Existing credentials retain their signed
type and key ID; both exact key IDs and types are accepted against the same
pinned issuer key. Arbitrary issuers and type URLs are not trusted or fetched.
Wallets must support this verifier-identification method. Update the supplier
binary with this change so its challenge validation accepts the final prefix.

The wallet submits form-encoded `state` and DCQL `vp_token` to
`POST /presentations/<id>/response`. The verifier checks issuer signature and
identity, credential type/expiry/demo label, selective disclosure, holder key,
key-binding signature, `sd_hash`, nonce, audience, issued-at freshness and state.
Replay and concurrent duplicate submission are rejected. Only task IDs, expiry and
a verified boolean are retained; no credential/presentation is retained or logged.
`GET /admin/presentations/<id>` requires the admin token and returns that task-bound
status. The supplier checks it when continuing the original paused purchase task.

The phone's already-issued credential remains valid if the issuer signing key is
unchanged. Redeployment or restart clears presentation sessions. Actual Sphereon
0.8.0 presentation acceptance is not established by the automated software-holder
tests and must be checked with the phone. Issuance still sets the demo `over_21`
claim without age checks: this is not a production age-verification service.

| Endpoint | Method | Purpose |
|---|---|---|
| `/health` | GET | Safe `{status:"ok",demo_only:true}`; no secrets/state |
| `/` | GET | Safe demo notice; no issuance controls |
| `/.well-known/openid-credential-issuer` | GET | Issuer/configuration metadata |
| `/.well-known/oauth-authorization-server` | GET | OAuth pre-authorized metadata |
| `/.well-known/openid-configuration` | GET | Same metadata for wallet discovery compatibility; not a full OIDC provider |
| `/.well-known/jwt-vc-issuer` | GET | HTTPS issuer public signing-key discovery |
| `/jwks` | GET | Public ES256 JWK under current and legacy key IDs |
| `/credential-types/age-over-21` | GET | SD-JWT VC type metadata |
| `/admin/offers` | POST | Bearer admin token + JSON `{}` → offer URLs |
| `/offers/<random-id>` | GET | Capability page and embedded QR |
| `/credential-offer/<random-id>` | GET | Raw credential offer JSON |
| `/token` | POST | Form `grant_type` and `pre-authorized_code` → Bearer token + nonce |
| `/nonce` | POST | Empty form, empty JSON object, or empty body → one-use nonce |
| `/credential` | POST | Bearer access token + configuration ID and JWT proof → credential array |

Token/offer lifetime: 10 minutes. Nonce lifetime: 5 minutes. Code redemption and
credential issuance are single-use, including concurrent requests. After token
redemption, the raw credential-offer endpoint returns 404; the private HTML page
retains only progress until its original expiry. A token can issue one credential.
Nonces obtained anonymously are usable with a valid access token; token-response
nonces and authenticated `/nonce` responses are bound to that token. Replacing a
token nonce invalidates its previous nonce.

Invalid proofs/nonces receive `invalid_proof`/`invalid_nonce` with a fresh
`c_nonce` and `c_nonce_expires_in`; retries must sign a new proof. Expired,
unknown or used access tokens get HTTP 401 `invalid_token`. Invalid/redeemed
pre-authorized codes get HTTP 400 `invalid_grant`. Other OAuth-style errors
include `invalid_request`, `invalid_scope`, `unsupported_grant_type` and
`unknown_credential_configuration`. Simultaneous issuance with the same token
can receive HTTP 409 while verification is in flight.

## Azure App Service deployment settings

The dedicated demo service is **https://agentcon-issuer.azurewebsites.net**,
in resource group `agentcon-issuer-rg`, Linux B1 plan `agentcon-issuer-plan`,
with Node 24 LTS. Resource creation was handled separately; this repository's
tests do not assert successful live deployment or mobile issuance.

Manual verification on October 10, 2026: the deployed HTTPS issuer served a
QR offer, and Darrel confirmed accepting the credential into Sphereon Wallet
0.8.0 on his phone. This confirms the issuance path, not OID4VP presentation
or supplier task resumption. On October 11, Darrel additionally confirmed
selecting/sharing the phone credential, verified status `true`, and completion
of the original pending supplier purchase with the signed did:jwk request.
That phone check used the merged baseline, not an automated software-holder test.

The experimental native buyer path is separately covered by the local
cross-language wallet integration in `test/presentation.test.mjs`. Set
`AGENTCON_SUPPLIER_EXE` and `AGENTCON_BUYER_EXE` to isolated built executables
to run both supplier and buyer quote/purchase/resume paths without inference.
Tests start their own ephemeral ports/verifier and never deploy this application
or alter the live issuer's key/settings. See the buyer README for build commands.

Deploy **only this directory's application files**, with `package.json` at the
ZIP root, to a Linux Node 22/24 App Service. The parent workflow handles Azure
inspection/provisioning/deployment; this application does not provision Azure.

Required application settings:

| Setting | Value |
|---|---|
| `ISSUER_BASE_URL` | `https://agentcon-issuer.azurewebsites.net` |
| `ISSUER_ADMIN_TOKEN` | Protected random 32–256 character base64url secret |
| `ISSUER_PRIVATE_JWK` **or** `ISSUER_PRIVATE_JWK_PATH` | Persistent private ES256 JWK, never both; prefer protected JSON/Key Vault reference |
| `SCM_DO_BUILD_DURING_DEPLOYMENT` | `true` when Azure must install dependencies from the ZIP |
| `PORT` | Use App Service's injected value; local default is 3000 |

Configure the Linux Node runtime to an available **22 LTS or 24 LTS** stack,
startup command `npm start`, **HTTPS Only enabled**, `/health` as the health-check
path, and **one instance/one Node process** (no clustered PM2). Do not enable a
second worker for this demo. Keep signing-key settings stable across deployments.
The service cannot establish whether a plain internal HTTP connection originally
arrived over HTTPS, and intentionally does not trust arbitrary forwarding headers:
HTTPS enforcement belongs at the trusted Azure front end.

For a source ZIP, do not include Windows `node_modules`, private key files, `.env`,
test artifacts, or existing credential outputs:

```powershell
Set-Location .\IssuerAgent
New-Item -ItemType Directory -Force .\local-output | Out-Null
Compress-Archive -Force -Path .\package.json, .\package-lock.json, .\*.mjs `
  -DestinationPath .\local-output\issuer.zip
```

Azure must install the locked dependencies on Linux (or package Linux-installed
production dependencies separately). The app has no dependency on files outside
this directory except an explicitly configured private-key file.

### In-memory demo limitations and operational safety

All offers/codes/tokens/nonces/rate buckets live in bounded process-local maps
(1,000 entries each), swept every 30 seconds and on requests. Capacity exhaustion
gets 503; expired entries free capacity. Request bodies are bounded to 16 KiB,
proofs to 8 KiB, header/body timeouts to 10 seconds, and requests to 120 per
minute per observed connection IP. Because proxy forwarding headers are not
trusted, Azure proxy/NAT users can share a rate bucket. Health checks bypass the
rate limiter. All responses are `no-store`; offer pages use `no-referrer`, CSP,
escaped HTML and local assets.

Restart, deploy, scale-out or multiple processes **lose or split issuance state**.
Create a fresh offer after restart. Existing signed credentials still verify as
long as the same public signing key remains published. A failed signing operation
after capability consumption burns that token/nonce; request a new admin offer.
For production, replace maps with transactional shared storage and add real
identity/age validation, stronger operational controls, revocation/status,
auditing with redaction, and a full protocol/security review.

Do not enable unredacted HTTP access logging, Application Insights URL capture,
proxy request-body logging or dependency logging for this demo: offer paths are
capabilities and proof/token/credential bodies are sensitive. The application
does not log URLs, headers, bodies, tokens, credentials or keys. Never put admin
tokens in URLs. Repository ignores and ZIP allowlists are precautions, not a
replacement for secret storage.

## Validation

```powershell
Set-Location .\IssuerAgent
npm test
```

Uses Node's built-in test runner, ephemeral in-memory test keys, real HTTP
requests and cryptographic proof/credential validation. Tests cover metadata,
selective disclosure, holder binding, public JWKS, draft-15/final request shapes,
offline `did:jwk`/`did:key` holder proofs, CORS/preflight and admin isolation,
HTTPS type/key discovery, legacy credential verification, bad signatures/audience/nonce/typ/algorithm/keys,
timestamp expiry, nonce/proof/token/code replay, parallel redemption/issuance,
admin protection, payload bounds, bounded-state cleanup, rate limiting, escaped
themed UI, and startup validation. Test HTTP runs on loopback; the configured
public origin remains HTTPS. No external wallet or Azure account is used.
