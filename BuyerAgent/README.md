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
[`catalog.json`](../catalog.json). That catalog currently lists only the buyer
itself, so a supplier search reports an explicit "no matches" result; the buyer
does not invent a quote or simulate a supplier.

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
a2a send -a http://localhost:5100 --timeout 150s 'Explain your role in the wine demo. The catalog lists no supplier yet; do not invent a quote.'
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
a2a send -a $buyer.url --timeout 150s --context-id buyer-smoke 'Explain your role in the wine demo. The catalog lists no supplier yet; do not invent a quote.'
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

Structured input is accepted without inventing a supplier-specific schema:

```powershell
a2a send -a http://localhost:5100 --timeout 150s --context-id dinner-party --text-part 'Request a quote using this data; do not place an order.' --data-part '{"wine":"Barolo","vintage":2019,"cases":12,"deliveryCity":"San Jose"}'
```

These fields are illustrative, not an agreed UBL profile. The Copilot harness
interprets the caller's instruction and constructs the supplier tool call.
This is not a zero-inference pass-through endpoint or a measured performance
comparison.

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
  `get_supplier_task` are exposed.
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
- Supplier artifacts and raw responses are returned without LLM rewriting.
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
