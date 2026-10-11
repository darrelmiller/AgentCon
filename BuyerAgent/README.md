# AgentCon buyer agent

An ASP.NET Core A2A v1 service with a GitHub Copilot SDK agent harness and
outbound A2A supplier tools:

```text
GitHub Copilot CLI
  -> official a2a CLI
  -> BuyerAgent (A2A JSON-RPC)
  -> GitHub Copilot SDK agent loop
  -> supplier (A2A JSON-RPC)
```

Microsoft Agent Framework is deliberately not included. Copilot SDK already
provides the agent/tool loop; A2A's .NET SDK supplies the server, client, cards,
messages, artifacts, task lifecycle and streaming. `Microsoft.Extensions.AI`
tool declarations arrive through the Copilot SDK dependency, not another agent
framework.

The packages are pinned to `A2A` / `A2A.AspNetCore` **1.0.0-preview2** and
`GitHub.Copilot.SDK` **1.0.19**. The A2A v1 .NET SDK is still a preview.

## Distributed tracing

See [the shared OTLP/Aspire setup](../docs/telemetry.md) for exact environment
variables, span semantics, privacy limits and cross-language validation.
Export is deliberately disabled when no OTLP endpoint is configured.

## Live exchange dashboard

To show actual buyer/supplier messages during a demo, explicitly enable local
content capture:

```powershell
dotnet run --project BuyerAgent\BuyerAgent.csproj --artifacts-path .\artifacts\exchange-dotnet -- --Buyer:ExchangeCaptureEnabled=true
Start-Process 'http://localhost:5100/demo'
```

The buyer appears on the left, the catalog seller on the right, and one
chronological conversation in the center. Entries are labelled **Buyer -> Seller**
or **Seller -> Buyer** and ordered by request send / response arrival time, even
when requests overlap. **Click a message** to inspect its
text or JSON data in the bottom panel; artifact buttons select individual
quote/order/receipt parts. **Raw wire body** shows the HTTP JSON-RPC body
(JSON pretty-printed) or SSE frames. Trace links open the matching Aspire
trace in an external browser. Selecting a seller in this viewer only filters
the view; it does not change any buyer conversation's supplier selection.
Click either agent's icon/name to open its full current Agent Card JSON in a
popup, including input/output modes, skills and extensions. The seller card is
fetched through the buyer from the selected catalog entry, not directly from the
browser. Close with **Close** or Escape; reopen to refresh after a mode change.

Each conversation bubble shows **LLM calls** from the original A2A message's
`metadata["agentcon.llmCalls"]`. The count comes from actual Copilot SDK
`assistant.turn_start` and retry events, not estimated tokens or calls to the
harness. For buyer sends it counts calls since the previous send in that buyer
turn (including catalog/selection reasoning). For seller replies it counts calls
for that request; deterministic structured processing reports zero. Re-reading
a task preserves the original message's count, not the cost of the GET. A missing
counter is shown as **not reported**, never assumed to be zero. A lagged seller
SDK event stream sets `agentcon.llmCallsComplete:false` and shows an incomplete
count warning rather than claiming an exact count. These are
per-message counts, not a claim that the buyer or transaction used no inference.

The handler observes the actual outbound A2A SDK HTTP content, not inferred
tool arguments. JSON and SSE pass through unchanged; SSE content appears as
the SDK consumes it, without buffering the whole stream. Discovery/card reads
and dashboard health probes are not captured. Buyer tasks use real handler
lifecycle states; seller tasks show the **latest observed** A2A status, which
can be stale until another response arrives. The dashboard never polls tasks,
places orders or invokes Copilot. A health check proves HTTP readiness only;
an in-flight request without a returned task ID is not presented as an invented
seller task. Direct calls to the seller that bypass this buyer are not captured.

Capture and `/demo` routes are **disabled by default**. Enabling capture stores
message bodies in bounded process memory: the newest 100 exchanges, at most
64 KiB per request/response body, and 128 recent observed tasks. Truncation,
partial stream consumption, HTTP errors and transport errors are visible.
Bodies may contain private information: use demo data only. Headers,
credentials in headers, internal Copilot prompts/reasoning and exception
messages are not captured; secrets placed inside an A2A body would be visible.
Nothing is written to disk or added to OTEL.

**Clear captured content** erases the retained dashboard content, not the
agents' tasks, quotes, orders or Copilot sessions. Late responses to cleared
exchanges do not restore their payloads. **Pause view** pauses refresh only;
capture continues. Restarting the buyer also clears the store. The view is
loopback-only, rejects cross-origin access, uses no-store responses and renders
untrusted payloads as text, never HTML. It has no production authentication
and must not be exposed through a proxy.

## Prerequisites

- .NET 10 SDK.
- A GitHub account with Copilot access and permitted model usage.
- Copilot CLI authenticated with `copilot login` (not the retired `gh copilot`
  extension). The SDK runs its own bundled, version-pinned Copilot runtime.
- The official `a2a` CLI on PATH to call the buyer from the outer Copilot CLI.
  See the [installation instructions](https://github.com/a2aproject/a2a-cli#installation).

The harness uses GitHub/Copilot authentication rather than an OpenAI or Azure
inference API key. This is Copilot-mediated inference under your account's
subscription, policies and usage limits, not general-purpose access to arbitrary
LLM inference APIs.

## Run

From the repository root in PowerShell:

```powershell
dotnet run --project BuyerAgent\BuyerAgent.csproj
```

No supplier is needed to start the buyer or discuss the demo. The buyer finds
suppliers by searching its configured AI Catalog, by default the repository
[`catalog.json`](../catalog.json). The catalog now includes the local
[Rust supplier](../SupplierAgent/README.md) as
`urn:agentcon:agent:demo-wine-supplier`. Start it before selecting it; the buyer
does not invent a quote when the supplier is unavailable.

When a supplier is available, publish its Agent Card URL as a catalog entry
rather than configuring a destination on the buyer. For example, add to
`entries` in `catalog.json`:

```json
{
  "identifier": "urn:air:<supplier-domain>:agent:wine-supplier",
  "type": "application/a2a-agent-card+json",
  "url": "http://localhost:6000/.well-known/agent-card.json",
  "description": "Wine supplier",
  "tags": ["wine", "supplier"]
}
```

The catalog is re-read on every search and selection, so no buyer restart is
needed after editing it. To use a different catalog, set `Buyer:CatalogUrl`:

```powershell
$env:Buyer__CatalogUrl = 'C:\demo\catalog.json'                 # or file:///C:/demo/catalog.json
$env:Buyer__CatalogUrl = 'http://localhost:7000/catalog.json'     # HTTP loopback
$env:Buyer__CatalogUrl = 'https://catalog.example'                # -> /.well-known/ai-catalog.json
dotnet run --project BuyerAgent\BuyerAgent.csproj
```

Relative paths resolve against the buyer's content root (normally the
directory `dotnet run` starts in). `Buyer:SupplierBaseUrl` was removed; startup
fails if it is still set, so a stale environment variable cannot silently
change the destination.

### Supplier discovery and selection

The Copilot harness exposes catalog tools rather than a fixed destination:

1. `search_supplier_catalog` lists A2A Agent Card entries
   (`application/a2a-agent-card+json`) whose `identifier`, `displayName`,
   `description` or `tags` match the query, or all of them for an empty query.
   It selects nothing. No match is a valid, explicit result.
2. `select_supplier` takes an exact catalog `identifier` (plus `version` when
   the identifier is ambiguous), fetches that entry's Agent Card through the A2A
   SDK, and validates it. With several matches the buyer asks the caller to
   choose an identifier rather than taking the first match.
3. `send_supplier_message` / `get_supplier_task` talk to the selected supplier.

The selection is per buyer `--context-id`. Supplier task and context IDs stay
bound to the supplier that issued them: continuing or inspecting an earlier
task after switching suppliers still goes to its original supplier, and an ID
reported by two suppliers requires `supplierIdentifier`.

Destination rules:

- Only Agent Card URLs discovered in the catalog can become destinations; the
  tools accept no URL input. Entries pointing at this buyer are excluded, and
  inline (`data`) Agent Cards are reported but not used.
- Catalog, card and interface URLs must be HTTPS, or HTTP on loopback, without
  credentials or fragments. `Buyer:AllowInsecureSupplierHttp` is the explicit
  opt-in for plaintext HTTP to a trusted LAN catalog or supplier.
- The card's A2A 1.0 `JSONRPC` interface must be on the same origin as the
  catalog's card URL. Redirects and cookies are disabled.
- Nested catalogs (`application/ai-catalog+json`) are followed up to depth 4,
  16 catalogs and 500 entries, with cycle detection. A remote catalog cannot
  reference local files, UNC paths are rejected, and documents are capped at
  1 MiB. An unreadable nested catalog is reported as a warning.
- A missing or malformed root catalog (`specVersion` other than `1.0`, missing
  `entries`, entries without `identifier`/`type` or exactly one of
  `url`/`data`, duplicates) or a network failure fails the buyer task explicitly.
- Catalog and card text is treated as untrusted data and truncated, never as
  instructions.

The buyer listens on `http://localhost:5100`:

| Endpoint | Purpose |
| --- | --- |
| `/.well-known/agent-card.json` | Agent Card discovery |
| `/a2a` | A2A v1 JSON-RPC, including SSE |
| `/health` | Process health; does not invoke Copilot or probe the supplier |

Call it directly, or ask your outer Copilot CLI to run these commands:

```powershell
# If using the demo-local installation of the official CLI:
$env:PATH = (Join-Path $env:LOCALAPPDATA 'AgentConDemo\bin') + ';' + $env:PATH
a2a card get http://localhost:5100 -o json
a2a send -a http://localhost:5100 --timeout 150s 'Explain your role in the wine demo. Do not request a quote or purchase.'
```

### Discover through the repository Catalog

[`catalog.json`](../catalog.json) is a local AI Catalog with a buyer entry
pointing at the live Agent Card. It is not a cached card or a supplier mock.
Run from the repository root in a second PowerShell terminal while the buyer
runs. The optional [AI Catalog CLI](https://github.com/Agent-Card/ai-catalog-cli)
can validate the file:

```powershell
ai-catalog validate .\catalog.json
if ($LASTEXITCODE -ne 0) { throw 'Catalog validation failed' }
$catalog = Get-Content .\catalog.json -Raw | ConvertFrom-Json
$buyers = @($catalog.entries | Where-Object {
    $_.identifier -eq 'urn:agentcon:agent:buyer' -and
    $_.type -eq 'application/a2a-agent-card+json'
})
if ($buyers.Count -ne 1) { throw 'Expected exactly one buyer Agent Card entry' }
$buyer = $buyers[0]
a2a card get $buyer.url -o json
a2a send -a $buyer.url --timeout 150s --context-id buyer-smoke 'Explain your role in the wine demo. Do not request a quote or purchase.'
```

This resolves Catalog entry -> Agent Card -> advertised JSON-RPC interface.
The A2A CLI does not itself resolve AI Catalogs; the buyer resolves supplier
entries from the same file itself. No global catalog registration
is needed. PowerShell reads the file directly because the installed AI Catalog
CLI's `show --scope file:///C:/...` fails to resolve Windows drive paths.

### Supplier messages

Once a supplier entry is in the catalog:

```powershell
a2a send -a http://localhost:5100 --timeout 150s --context-id dinner-party 'Search the catalog for wine suppliers and list them. Do not select one yet.'
a2a send -a http://localhost:5100 --timeout 150s --context-id dinner-party 'Select urn:air:<supplier-domain>:agent:wine-supplier and ask it for a quote for 12 cases of 2019 Barolo delivered to San Jose.'
a2a send -a http://localhost:5100 --timeout 150s --stream --context-id dinner-party 'Summarize the quote. Do not purchase yet.'
```

### Supplier communication modes

The buyer accepts only `text/plain` caller input in every communication mode,
advertised globally and on its skill. JSON data parts and other declared input
media types are rejected before inference. Communication modes govern the
buyer's exchanges with sellers, not its caller input format.

`Buyer:CommunicationMode` sets the initial mode (also `Buyer__CommunicationMode`
in the environment). The Buyer Agent section of `/demo` switches modes without
restarting. The change applies to new turns; active turns retain a snapshot of
their mode and contracts. Dashboard changes are in memory, not saved to configuration.
The live Agent Card updates immediately. The three values are:

| Mode | Supplier requests | Contract knowledge |
| --- | --- | --- |
| `NaturalLanguage` (default) | Only `text/plain` | Seller must advertise text input; no schema fetch and JSON tool arguments are rejected |
| `LearnStructures` | Typed JSON data parts only | Selection reads the seller's schema-discovery extension, fetches its JSON Schema, and validates requests and structured responses at runtime |
| `Native` | Typed JSON data parts only | Installed wine-v1 RFQ/order/quote/receipt records, serialization/deserialization and validation; no schema fetch |

```powershell
dotnet run --project BuyerAgent\BuyerAgent.csproj -- --Buyer:CommunicationMode=LearnStructures --Buyer:ExchangeCaptureEnabled=true
# Or --Buyer:CommunicationMode=NaturalLanguage / --Buyer:CommunicationMode=Native
```

The host enforces the mode at the gateway as well as in Copilot instructions.
The send tool must provide an exact `mediaType` for structured data; it may only
use advertised input types with a learned schema or installed codec. There is
no generic `application/json` or text fallback in structured modes. The select
tool returns understood schemas so the harness can construct a conforming
request, rather than guessing a structure from a media-type name. Native code
also checks business constraints and quote totals. Unknown native types fail
explicitly. Native still uses Copilot to interpret the buyer's request and
choose tools; it is not an end-to-end zero-inference buyer.

LearnStructures uses the demo-only, unregistered Agent Card extension
`urn:agentcon:extension:structured-schemas:v1`. Its `params.schemas` entries
contain `mediaType`, `direction` (`input`/`output`), a same-origin absolute `url`,
and a local `$defs` `definition`. Each unique document is fetched once per
selection (64 KiB maximum; at most eight descriptors), using the existing
redirect-disabled client. External schema references are rejected.
Learned formats are bound to the selected supplier, including task continuations
after changing suppliers. They are untrusted data, not new agent instructions.

NaturalLanguage advertises only `text/plain` outputs globally and on its skill,
requests only `text/plain` through A2A `configuration.acceptedOutputModes`, and
rejects structured seller replies. It does not emit the raw JSON supplier-response
artifact. The seller renders factual quote/order text instead of typed artifacts.
Structured modes request text plus only the output formats they understand.
The wine-v1 RFQ supports any city in the US or Canada through `deliveryCity`
and optional `deliveryCountry` (`US`/`CA`, default `US`). Native codecs preserve
the country in requests and quote artifacts; LearnStructures fetches the updated
seller schema. Both countries use USD pricing and the same flat delivery fee.
LearnStructures and Native validate structured output on both sends and task
reads before exposing it to the harness. An invalid order response does not
imply rollback; inspect the seller task before retrying.

In **LearnStructures or Native**, request a structured demo quote:

```powershell
a2a send -a http://localhost:5100 --timeout 150s --context-id dinner-party 'Select urn:agentcon:agent:demo-wine-supplier and use its understood input schema to quote 12 cases of 2019 Barolo delivered to San Jose. Do not purchase.'
```

These fields follow the local supplier's [agreed demo profile](../SupplierAgent/README.md#agreed-structured-profiles),
not UBL. The Copilot harness interprets the caller's instruction and constructs
the supplier tool call. The supplier handles this profile deterministically,
but this buyer endpoint is not zero-inference or a measured performance comparison.

Reuse `--context-id` across completed buyer tasks to retain recent conversation
history. Use `--task-id` to continue a buyer task paused for input or authorization:

```powershell
a2a task get -a http://localhost:5100 <buyer-task-id> --wait --timeout 150s -o json
a2a send -a http://localhost:5100 --task-id <buyer-task-id> --timeout 150s 'Check the supplier task now that verification has finished.'
```

## Configuration

Use ASP.NET Core configuration, e.g. `Buyer__Model` in the environment or
`--Buyer:Model=auto` on the command line. Optional `appsettings.Local.json` is
ignored by Git.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Buyer:ListenUrl` | `http://localhost:5100` | HTTP loopback origin only |
| `Buyer:Model` | `auto` | Copilot model selection; subject to account policy |
| `Buyer:CommunicationMode` | `NaturalLanguage` | Initial mode: `NaturalLanguage`, `LearnStructures`, or `Native`; change live in `/demo` |
| `Buyer:ExchangeCaptureEnabled` | `false` | Enable local in-memory exchange capture and `/demo` dashboard |
| `Buyer:CatalogUrl` | repository `catalog.json` | AI Catalog to search: local path (absolute, or relative to the content root), `file:` URI, or HTTPS / loopback HTTP URL. An origin-only URL means `/.well-known/ai-catalog.json` |
| `Buyer:AllowInsecureSupplierHttp` | `false` | Explicitly permits plaintext HTTP to a trusted LAN catalog, Agent Card or supplier |
| `Buyer:RequestTimeoutSeconds` | `120` | Buyer inference/tool execution deadline, 1-600 seconds |
| `Buyer:MaxConversations` | `32` | Maximum distinct in-memory contexts, 1-128 |
| `Buyer:CopilotStateDirectory` | `%LOCALAPPDATA%\AgentConDemo\buyer-copilot` | Absolute directory for isolated runtime state, outside the repo by default |

For Luca's laptop over plaintext LAN HTTP, list his card URL
(`http://<luca-laptop-address>:6000/.well-known/agent-card.json`) in the
catalog and opt in deliberately:

```powershell
$env:Buyer__AllowInsecureSupplierHttp = 'true'
```

If authentication is unavailable in the isolated runtime, explicitly log into
its state directory in a separate PowerShell session:

```powershell
$env:COPILOT_HOME = Join-Path $env:LOCALAPPDATA 'AgentConDemo\buyer-copilot'
$env:COPILOT_DISABLE_KEYTAR = '1'
copilot login
```

That fallback can store credentials in the private state directory rather than
the system keychain. Protect that directory; do not copy it into Git.
Alternatively, the SDK supports a suitable token through `COPILOT_GITHUB_TOKEN`;
never put a token in appsettings or a command-line argument.

## Behavior and limits

- `CopilotClientMode.Empty` disables ambient coding-agent features. Only
  `search_supplier_catalog`, `select_supplier`, `send_supplier_message` and
  `get_supplier_task` and `assess_supplier_quote` are exposed.
  Other permission requests are denied. Persistent memory and cross-session
  search are disabled.
- Each turn uses a fresh Copilot session. The host replays the last eight buyer
  turns and supplier results for its context, and serializes turns within that
  context. This state and the A2A task store are in memory and disappear on restart.
  At the context limit, new contexts fail explicitly rather than evicting state.
- Only suppliers explicitly selected from catalog entries are destinations.
  Card endpoints must stay on the card URL's origin. Cookies and automatic
  redirects are disabled. See [Supplier discovery and selection](#supplier-discovery-and-selection).
- Supplier tasks are returned immediately; the harness can inspect their state.
  Still-running supplier tasks produce buyer `input-required`, rather than
  claiming the order is complete. No infinite polling loop runs.
- Supplier quote/receipt artifacts are returned without LLM rewriting, as text
  in NaturalLanguage or typed data in structured modes. Raw response envelopes
  remain in the wire viewer/history, not extra task artifacts. If supplier
  artifacts exist, buyer completion prose stays in the status message rather
  than adding a duplicate summary artifact.
  Supplier failures and tool failures cannot be converted to successful buyer
  completion simply because the model produces reassuring prose.
- Supplier `auth-required` messages relay OID4VP metadata to the caller. This
  **does not invoke a phone wallet, render a QR code, issue credentials, or run a
  verifier**. After external verification, explicitly ask the buyer to inspect
  the supplier task. Supplier completion does not automatically resume the buyer.
- Cancellation stops buyer execution, not orders already accepted at the
  supplier. After cancellation, timeout or an uncertain transport failure,
  inspect supplier state before retrying an order.
- This is a **local, single-user demo service**, not a production purchasing
  agent. It has no caller authentication, payment integration, durable state or
  independent human-approval workflow. The model is instructed to require an
  explicit purchase request; that is not a production authorization boundary.
  Do not connect it to real purchasing infrastructure.
- Browser-origin requests are rejected. Do not expose or reverse-proxy the
  unauthenticated service outside the local machine.
- The repository Catalog discovers the buyer through the outer CLI workflow,
  and the buyer searches it for suppliers. SLIMRPC, QR hand-off and automatic
  verifier-to-buyer resumption remain separate demo work. Phone issuance is
  implemented separately in `IssuerAgent`.

## Development

### Quote and purchase task boundaries

A quote-only request obtains a quote artifact and completes; it never orders.
An explicit buy request obtains a fresh quote, assesses its price, and attempts
the simulated order only when the assessment passes. `assess_supplier_quote` takes an observed quote ID and optional caller budget;
the host reads its actual prices from typed data or the seller's known demo text
quote format, rather than trusting model-reconstructed numbers. Unknown text
formats require caller input. It checks arithmetic, the budget, and a
configured **demo-only** 2019 Barolo range in USD:
`Buyer:DemoMinBottlePriceUsd=8` and `Buyer:DemoMaxBottlePriceUsd=30`.
These are illustrative demo bounds, **not researched market prices**. Unknown
wines/currencies or out-of-range prices require caller input, not an automatic
order. Purchase intent and caller budget extraction remain buyer-agent behavior,
not a production authorization mechanism.

Supplier task state defines continuation: completed/failed/rejected/canceled
tasks cannot receive further messages. To order after a completed quoting task,
the buyer omits `taskId` and retains `contextId` and the quote ID. If a seller
asks for an RFQ or order with `input-required`, the buyer can continue that same
nonterminal task, allowing an entire sale within one seller task. GetTask remains
available for terminal tasks; task ownership survives mode and supplier changes.

```powershell
dotnet restore BuyerAgent.Tests\BuyerAgent.Tests.csproj --locked-mode
dotnet test BuyerAgent.Tests\BuyerAgent.Tests.csproj
```

Tests exercise the real A2A SDK's JSON-RPC and SSE endpoints with a deterministic
harness, plus outbound SDK requests, structured data, artifact preservation,
task/authorization states, cancellation, error handling and conversation isolation.
Catalog tests cover file paths/URIs, loopback and HTTPS catalogs, malformed
documents, redirects, nested catalogs, URL policy, explicit multi-supplier
selection and task routing after switching suppliers.
They do not spend Copilot inference requests.

References:
[A2A .NET SDK](https://github.com/a2aproject/a2a-dotnet),
[Copilot .NET SDK](https://github.com/github/copilot-sdk/tree/main/dotnet),
[Copilot authentication](https://github.com/github/copilot-sdk/blob/main/docs/auth/authenticate.md).
