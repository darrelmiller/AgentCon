# Demo age credential signing

This tool configures and signs a **test** age credential. It does not perform
real age verification, run an OID4VCI issuer, or deliver credentials to Sphereon.

The extension's Credo sample signs an SD-JWT into an embedded software wallet.
This tool similarly uses a local test holder, but generates fresh ES256 keys
instead of copying the sample's fixed secrets. The OpenWallet Foundation's
`@sd-jwt/sd-jwt-vc` library handles selective disclosure; Node handles signing.
The issuer is identified by a `did:jwk` derived from its public key; the verifier
still needs an explicit trust decision, not just a valid signature.

## Run (PowerShell, Node 22 or newer)

From the repository root:

```powershell
npm ci --prefix tools\credentials
$env:AGENTCON_CREDENTIAL_STATE_DIR = Join-Path $env:LOCALAPPDATA 'AgentConDemo\credentials'
npm run issue --prefix tools\credentials
```

Each run writes a new directory outside the repository; existing keys are never
overwritten. On Windows, Unix file modes do not restrict ACLs: use a private
user directory and protect the private files with Windows permissions or move
them to your chosen secure storage. Do not commit keys or credentials.

| File | Purpose |
| --- | --- |
| `issuer-private.jwk.json` | Secret signing key. Keep private. |
| `test-holder-private.jwk.json` | Secret software-holder key. Keep private. |
| `issuer-public.jwks.json` | Public key to explicitly trust in the demo verifier. |
| `test-holder-public.jwk.json` | Public key bound into this test credential. |
| `age-credential.sd-jwt` | Signed credential with the age disclosure. Treat as private. |
| `credential-summary.json` | Verified decoded claims and demo warning. |

`age-credential.json` defines the credential type, demo label, `over_21` assertion,
and 14-day lifetime. `over_21` is selectively disclosable. The credential also
reveals issuer, type, lifetime, demo label, and holder public key: "only over 21"
means only that personal claim, not literally a one-field network response.

The credential uses `dc+sd-jwt`, as emitted by the pinned library. The extension's
current sample query uses the older `vc+sd-jwt` format identifier and a different
credential type. Do not assume this credential works with that unchanged sample.
Confirm wallet/verifier versions and configure their format, type, claim path,
issuer trust, and holder-binding checks together.

## Phone-wallet issuance is a separate step

This credential is bound to the generated software-holder key, **not** the phone's
key. Do not copy the holder private key to Sphereon as an import workaround.
For the phone demo, an OID4VCI issuance flow must obtain the wallet's holder
key/proof and sign a new credential bound to it. The verifier must validate
holder binding, nonce and audience in the OID4VP presentation, not merely the
issuer signature.

The script verifies the issuer signature, issuer/type, expiry and expected claims
before saving. Run its local tests with:

```powershell
npm test --prefix tools\credentials
```

Reference: [OID4VP extension sample](https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/sample).
