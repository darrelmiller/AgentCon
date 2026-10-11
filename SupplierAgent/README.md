# Rust wine supplier

**Test only.** This supplier quotes invented 2019 Barolo inventory and records
simulated purchase receipts. Nothing purchases wine, charges money, ships
anything, validates credentials or establishes buying authority.

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

```powershell
# Keep vcvars and cargo in the same cmd.exe process.
& $env:ComSpec /c 'set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%" && call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul && cargo +1.98.1-x86_64-pc-windows-msvc build --manifest-path SupplierAgent\Cargo.toml --locked'
.\SupplierAgent\target\debug\agentcon-supplier.exe
```

On a normally configured native Rust/MSVC installation, `cargo run
--manifest-path SupplierAgent\Cargo.toml --locked` suffices. Health/discovery
and structured requests need **no Copilot login or inference**.

| Endpoint | Purpose |
| --- | --- |
| `http://localhost:6000/health` | Readiness, structured requests and inference **attempts** |
| `http://localhost:6000/.well-known/agent-card.json` | SDK-served live A2A 1.0 Agent Card |
| `http://localhost:6000/a2a` | SDK JSON-RPC, including task get/list/cancel and SSE |

Only `127.0.0.1` is bound. Startup fails if the port is occupied; do not kill an
existing process. Browser-origin/fetch requests are rejected. This is an
unauthenticated, single-user local demo: do not expose or reverse-proxy it.

Environment options (scope them to the service process):
`SUPPLIER_PORT` (default `6000`), `SUPPLIER_MODEL` (default `auto`),
`SUPPLIER_COPILOT_STATE` (absolute directory outside the repository; default
`%LOCALAPPDATA%\AgentConDemo\supplier-copilot`).

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
For explicit real purchases or requests to verify age, the task reports
`TASK_STATE_AUTH_REQUIRED` with an honest **not implemented** explanation.
It does not advertise or activate an OID4VP extension, produce a QR code or
accept a user's age assertion as proof. Supplying missing values to
`TASK_STATE_INPUT_REQUIRED` must include the full request in the next message.

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
Copilot. Accompanying text is commentary and **not interpreted**. For the
existing .NET buyer's `Part.FromData` tool, which does not set `mediaType`,
the same required `kind`/`schemaVersion` envelope is accepted with absent
media type or `application/json`. Unknown fields, mismatched media types,
unsupported versions, arbitrary JSON and file parts explicitly fail, never
fall back to inference.

Quotes are USD cents: `cases * 14400 + 2500`, 12 bottles/case, 1-48 cases,
2019 Barolo only, San Jose only; no taxes modeled. A 12-case quote totals
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

The script asserts USD 1753.00, simulated/no-verification receipt and no change
in supplier inference attempts. It measures the transaction only, excluding
card/setup. Run without concurrent NL traffic. This demonstrates zero
**supplier** inference; the existing buyer service still invokes its Copilot
harness when asked to use structured data. Do not claim an end-to-end
zero-LLM transaction through that service.

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

**Future work:** OID4VP presentation/verifier integration with the phone's
Sphereon 0.8.0 wallet, experimental authorization extension activation and
task resumption, SLIMRPC and a deterministic inbound buyer service route.
Phone receipt of the issuer's demo over_21 credential does **not** mean this
supplier has checked it. No issuer signing keys or wallet secrets are used.
