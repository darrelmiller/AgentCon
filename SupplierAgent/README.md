# Rust wine supplier

This demo supplier quotes 2019 Barolo and records simulated purchase receipts.

The HTTP server, Agent Card, JSON-RPC dispatch, protobuf-JSON wire conversion,
task store and SSE are the official
[`a2aproject/a2a-rs`](https://github.com/a2aproject/a2a-rs) SDK, not a homemade A2A
implementation. Crates.io packages verified against their official repository:
`a2a-lf = 0.4.1`, `a2a-server-lf = 0.5.1`, both exactly pinned, implementing
A2A **1.0**. Natural language uses the official
[`github/copilot-sdk` Rust SDK](https://github.com/github/copilot-sdk/tree/main/rust),
`github-copilot-sdk = 1.0.20-preview.2`, exactly pinned. `Cargo.lock` pins the
dependency graph.

## Distributed tracing

See [the shared OTLP/Aspire setup](../docs/telemetry.md). Rust HTTP server spans
extract W3C `traceparent`; the official A2A SDK executor keeps that parent across
its background queue. Structured work has agent/business spans, **not** chat
spans. Export is deliberately disabled without a configured OTLP endpoint.

## Run on this Windows ARM64 machine

Rust 1.94+ is required by Copilot. This machine has native ARM64 Rust but only
x64/x86 Visual C++ libraries. Use the **already installed**
`1.98.1-x86_64-pc-windows-msvc` toolchain and MSVC x64 cross-emulation environment;
do not change global PATH or the rustup default. The resulting x64 executable
and bundled x64 Copilot runtime run under Windows ARM64 emulation.

From the repository root in PowerShell:

SLIM's native dependencies use bindgen and require `libclang.dll` matching the
**Rust host** architecture, not the Windows OS architecture. For this machine's
x64 Rust build, install an isolated x64 library (requires Python with pip):

```powershell
python -m pip install --target "$env:LOCALAPPDATA\AgentConDemo\build-tools\libclang" --platform win_amd64 --only-binary=:all: --no-deps libclang==18.1.1
```

The location above is an example installation directory, not a launcher
convention. Set `LIBCLANG_PATH` in the current terminal to the directory
containing your installed DLL. Alternatively, the launcher discovers libclang
beside `clang.exe` on PATH. No global PATH or rustup changes are needed:

```powershell
$env:LIBCLANG_PATH = "$env:LOCALAPPDATA\AgentConDemo\build-tools\libclang\clang\native"
.\start-demo.ps1 -CopilotGhUser darrelmiller
```

The launcher discovers Visual Studio through `vswhere.exe`. On Windows ARM64
it uses an installed x64 MSVC Rust toolchain, or honors `RUSTUP_TOOLCHAIN`
when set; no Rust version or Visual Studio installation path is hard-coded.
It uses Ninja and configures CMake to identify the x64 target correctly on an
ARM64 host; Ninja must be on `PATH` or installed with Visual Studio's
CMake/Ninja component. An existing `CMAKE_TOOLCHAIN_FILE` is preserved. Install
the matching **MSVC x64/x86 Spectre-mitigated libraries** component through
Visual Studio Installer as well: SLIM's native dependencies require it.

For a manual build in this machine's existing toolchain:

```powershell
$env:CMAKE_GENERATOR = 'Ninja'
$env:CMAKE_TOOLCHAIN_FILE = Join-Path $PWD 'SupplierAgent\windows-x64.cmake'
# Keep vcvars and cargo in the same cmd.exe process.
& $env:ComSpec /c 'set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%" && call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul && cargo +1.98.1-x86_64-pc-windows-msvc build --manifest-path SupplierAgent\Cargo.toml --target-dir "%LOCALAPPDATA%\AgentConDemo\rust\manual" --locked'
& "$env:LOCALAPPDATA\AgentConDemo\rust\manual\debug\agentcon-supplier.exe"
```

On a normally configured native Rust/MSVC installation, `cargo run
--manifest-path SupplierAgent\Cargo.toml --locked` suffices. Health/discovery
and structured requests need **no Copilot login or inference**.

| Endpoint | Purpose |
| --- | --- |
| `http://localhost:6000/health` | Readiness, structured requests, inference **attempts** and cumulative agent LLM metrics |
| `http://localhost:6000/.well-known/agent-card.json` | SDK-served live A2A 1.0 Agent Card |
| `http://localhost:6000/a2a` | SDK JSON-RPC, including task get/list/cancel and SSE |
| `http://localhost:6000/a2a/rest` | SDK HTTP+JSON (REST), e.g. `/message:send`, `/message:stream`, `/tasks/{id}` |

Health's `llm` object reports a process `instanceId`, actual model `calls`,
`responseTimeMs`, `timedCalls` and event-stream `complete` status. Copilot time is
the sum of SDK usage durations; the OpenAI-compatible client measures the model
HTTP wait, including failed responses. Missing usage samples are not filled with
zero. These process totals are independent of message capture and transport;
the buyer dashboard applies per-agent Clear baselines without resetting suppliers.

Only `127.0.0.1` is bound. Startup fails if the port is occupied; do not kill an
existing process. Browser-origin/fetch requests are rejected. This is an
unauthenticated, single-user local demo: do not expose or reverse-proxy it.

Environment options (scope them to the service process):
`SUPPLIER_PORT` (default `6000`), `SUPPLIER_MODEL` (default `auto`),
`SUPPLIER_COPILOT_STATE` (absolute directory outside the repository; default
`%LOCALAPPDATA%\AgentConDemo\supplier-copilot`).

`SUPPLIER_STRUCTURED_DATA` is `true` by default (strict `true`/`false`).
The buyer dashboard's supplier checkbox can change it live. Configure matching
process-scoped `SUPPLIER_DEMO_CONTROL_TOKEN` and `Buyer__SupplierControlToken`
random base64url tokens (32-256 characters). Without a token the configuration
endpoint is disabled. The browser never gets the secret; the buyer selects the
control destination from a unique loopback catalog entry. `/demo/structures`
requires `Authorization: Bearer <token>` and the supplier's configured Host,
and rejects browser Origin/fetch headers. Do not reuse the issuer admin token
for this control in the demo.

When disabled, the live card and skill advertise only `text/plain`, the schema
extension is removed, `/schemas/wine-v1.schema.json` returns 404, typed requests
fail without inference, and outputs are text only. Generic `application/json`
and mixed text/data are never accepted, even on authorization continuations.
Each execution snapshots the setting when it begins; active text executions keep
their output contract. Pending authorized purchases remain immutable and still
require verifier approval; a text continuation can resume them when typed input
has been disabled. See the buyer README for automatic negotiation and the
separate installed, zero-inference **typed inbound buyer** path.

The Copilot SDK bundles its matching runtime and uses existing GitHub/Copilot
login, not issuer keys or an unrelated inference API. It lazily starts only on
the first natural-language request. Empty mode, no available tools, deny-all
permissions, no memory, no session store and no custom instructions isolate
it from the coding assistant. Authentication failures/timeouts/invalid model
output explicitly fail the A2A task; **no regex or template substitutes for
failed LLM interpretation**. The model extracts an intent; deterministic host
code validates it, prices it and renders factual prose and artifacts.

## Natural-language baseline

```powershell
a2a card get http://localhost:6000 -o json
a2a send -a http://localhost:6000 --timeout 150s --context-id dinner-demo 'Quote 12 cases of 2019 Barolo delivered to San Jose for the demo.'
a2a send -a http://localhost:6000 --timeout 150s --context-id dinner-demo 'Buy it as a simulated demo order.'
```

Each NL turn invokes Copilot. The last quote in that A2A context is supplied
to the interpreter for "buy it"; general chat history is not retained.
Every purchase-order intent, including deterministic typed purchases, pauses in
`TASK_STATE_AUTH_REQUIRED` before recording an order. Quotes remain unauthenticated.
The supplier advertises the experimental
[OID4VP in-task authorization extension](https://github.com/a2aproject/experimental-ext-oid4vp-auth/blob/main/v1/spec.md).
The status message's metadata, keyed by the extension URI, contains
`authorizationRequest: {client_id, request_uri}`. `agentcon.presentation` additionally
contains the QR page, wallet link, expiry, and supplier task/context IDs.
Supplying missing values to
`TASK_STATE_INPUT_REQUIRED` must include the full request in the next message.

### Wallet authorization

Deploy the updated `IssuerAgent` application to the existing HTTPS issuer, preserving
its signing key and admin token. Set **process-scoped** `SUPPLIER_VERIFIER_URL` to
`https://agentcon-issuer.azurewebsites.net` and `SUPPLIER_VERIFIER_TOKEN` to its
`ISSUER_ADMIN_TOKEN` secret. Both settings must be supplied together. The supplier
uses authenticated, redirect-disabled HTTPS requests to create a task-bound challenge
and retrieve its verified status. No verifier configuration means purchases stay
auth-required with an explicit configuration error, never a receipt.

Open the presentation page returned with the purchase task and scan its QR in
Sphereon. Select the existing issuer credential and consent to disclose `over_21`.
The verifier checks the pinned issuer signature, disclosure hash, credential expiry,
holder key signature, nonce, audience, state, and presentation expiry. It accepts
only the issuer's demo credential type and `demo_only:true`. This proves possession
of a signed demo assertion, **not independently verified age**.

With `SendStreamingMessage` over HTTP JSON-RPC, HTTP+JSON or SLIMRPC, the seller keeps the execution alive after
`auth_required` and checks authenticated task-bound verifier status once per
second until approval, expiry or cancellation. Approval executes the immutable
saved PO without inference, then emits `TaskArtifactUpdateEvent` and
`TaskStatusUpdateEvent` (`completed`). The buyer consumes these on the existing
connection and updates its task/dashboard automatically; no extra message is
needed. Capabilities/output formats stay snapshotted for this execution. A
disconnected streaming client does not revoke wallet consent or cancel the
supplier task; inspect its state before retrying.

For synchronous `SendMessage`, after wallet approval send a continuation on the **same supplier task and context**
(or ask the buyer to continue its auth-required task). The supplier checks verifier
status and executes the immutable purchase saved before the pause; replacement
purchase data and a caller's assertion of approval cannot authorize it. It does not
automatically resume on `GetTask` polling. Unverified continuations remain auth-required.
Challenges expire in 10 minutes; expired purchases need a new challenge and an
unexpired quote. No presentation JWTs, holder keys, or admin tokens enter A2A messages.
Text/plain status output is required for purchase authorization.

## Agreed structured profiles

These are **unregistered, demo-only vendor media types**, not IANA
registrations, UBL, or a claim that advertising a type teaches its schema.
Both parties must agree to [wine-v1.schema.json](schemas/wine-v1.schema.json)
and these semantics before transacting.

| A2A `Part.mediaType` | Payload `kind` | Direction |
| --- | --- | --- |
| `application/vnd.agentcon.demo.wine-rfq.v1+json` | `rfq` | Input |
| `application/vnd.agentcon.demo.wine-quote.v1+json` | `quote` | Artifact |
| `application/vnd.agentcon.demo.wine-purchase-order.v1+json` | `purchase-order` | Input |
| `application/vnd.agentcon.demo.wine-order-receipt.v1+json` | `order-receipt` | Artifact |

Use the SDK's actual A2A v1 `data` part with `mediaType`, not a fabricated
part discriminator or JSON embedded in a text part:

```json
{
  "data": {
    "kind": "rfq",
    "schemaVersion": "1",
    "demoOnly": true,
    "wine": "Barolo",
    "vintage": 2019,
    "cases": 12,
    "deliveryCity": "San Jose"
  },
  "mediaType": "application/vnd.agentcon.demo.wine-rfq.v1+json"
}
```

Exactly one data part is processed deterministically, without even starting
Copilot. Accompanying text is commentary and **not interpreted**. A data part
must explicitly declare the matching vendor `mediaType`: absent media types
and generic `application/json` are rejected, not inferred from the envelope.
The Agent Card advertises only `text/plain`, the RFQ type and purchase-order
type as accepted inputs, both globally and on its skill. Unknown fields, mismatched media types,
unsupported versions, arbitrary JSON and file parts explicitly fail, never
fall back to inference.

### Schema discovery

The seller respects each send's `configuration.acceptedOutputModes`, including
queued execution. Text-only callers receive a text quote/receipt artifact rather than typed data.
Typed-only callers receive the accepted quote/receipt artifact without a text
status message. Omitted modes retain the default outputs; empty or incompatible
modes fail with A2A `CONTENT_TYPE_NOT_SUPPORTED`. Incompatible order outputs are
checked before recording a simulated order. A bounded middleware bridge preserves
the configuration in executor metadata because the pinned SDK omits it from
`ExecutorContext`; it does not implement a replacement A2A transport.

The live Agent Card advertises the demo-only, unregistered
`urn:agentcon:extension:structured-schemas:v1` extension. Its `params.schemas`
maps each input/output media type to an HTTP URL and a local `$defs` definition.
`GET /schemas/wine-v1.schema.json` returns the agreed schema with
`Content-Type: application/schema+json`. This is discovery metadata, not an A2A
business task and not inference. The buyer's `LearnStructures` mode fetches and
validates these schemas before sending typed data. `Native` uses installed
wine-v1 codecs; `NaturalLanguage` sends only text. See the
[buyer modes](../BuyerAgent/README.md#supplier-communication-modes).

Quotes are USD cents: `cases * 14400 + 2500`, 12 bottles/case, 1-48 cases,
2019 Barolo only, delivery anywhere in the US or Canada; no taxes or duties modeled.
The USD 25 flat delivery fee applies in both countries; Canadian quotes are also
in USD. Set `deliveryCountry` to `US` or `CA`; existing RFQs without it default to
`US`. Any nonempty destination city (up to 128 characters) is accepted in the
declared country. Destinations are not geocoded or checked for legal wine-shipping
eligibility. A 12-case quote totals
**USD 1753.00**. Quotes expire after 15 minutes and are bound to their A2A
context. Order fields are `kind:"purchase-order"`, `schemaVersion:"1"`,
`demoOnly:true`, `quoteId`, `purchaseOrderId` (1-128 characters).
`purchaseOrderId` is idempotent per context: repeating it with the same quote
returns the original receipt; changing its quote fails. A new ID represents
a separate simulation. Quotes and orders have a 128-entry cap; all state is
in memory and disappears on restart. The SDK task store is also in memory.
Cancellation never claims to roll back an already recorded receipt.

Run the official CLI RFQ -> quote -> PO -> receipt -> GetTask check:

```powershell
.\SupplierAgent\Smoke-Structured.ps1
```

The script asserts USD 1753.00, an auth-required purchase without a receipt, and no change
in supplier inference attempts. It measures the transaction only, excluding
card/setup. Run without concurrent NL traffic. This demonstrates zero
**supplier** inference; the existing buyer service still invokes its Copilot
harness when asked to use structured data. Do not claim an end-to-end
zero-LLM transaction through that service.
Use `.\SupplierAgent\Smoke-Structured.ps1 -WaitForWallet` for the manual phone
roundtrip: it opens the QR page, waits for confirmation, then continues the same
task and requires a `verified-demo-over-21` simulated receipt.

## Catalog-selected buyer

`catalog.json` includes `urn:agentcon:agent:demo-wine-supplier`, pointing to
the **live card**, not a hardcoded buyer destination. The buyer re-reads its
catalog on search/selection; after integrating this commit its running
service needs no restart.

```powershell
a2a send -a http://localhost:5100 --timeout 150s --context-id buyer-dinner 'Search the catalog for wine suppliers, select urn:agentcon:agent:demo-wine-supplier, and ask for a demo quote for 12 cases of 2019 Barolo delivered to San Jose. Do not purchase.'
a2a send -a http://localhost:5100 --timeout 150s --context-id buyer-dinner 'Buy the quoted wine as a simulated demo order only.'
```

For an isolated buyer test, without changing another worktree's buyer/catalog:

```powershell
dotnet run --project BuyerAgent\BuyerAgent.csproj -- --Buyer:ListenUrl=http://localhost:5101 --Buyer:CatalogUrl="$PWD\catalog.json"
# Then use the same a2a commands with port 5101.
```

## Development and integration scope

```powershell
cargo +1.98.1-x86_64-pc-windows-msvc fmt --manifest-path SupplierAgent\Cargo.toml -- --check
& $env:ComSpec /c 'set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%" && call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul && cargo +1.98.1-x86_64-pc-windows-msvc check --manifest-path SupplierAgent\Cargo.toml --locked && cargo +1.98.1-x86_64-pc-windows-msvc test --manifest-path SupplierAgent\Cargo.toml --locked'
```

Tests use a real ephemeral HTTP listener and the SDK routes; the structured
interpreter panics if invoked, proving the route bypasses inference.

**Remaining interoperability work:** presentation with the phone's Sphereon
0.8.0 wallet must be manually tested; software-holder tests do not establish mobile
acceptance. SLIMRPC and a deterministic inbound buyer service route remain future work.
Phone receipt of the issuer's demo over_21 credential does **not** mean this
supplier has checked it. No issuer signing keys or wallet secrets are used.
