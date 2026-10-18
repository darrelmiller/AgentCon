# AgentCon San Jose 2026: A2A v1 Deep Dive

Demo materials for **"A2A v1 Deep Dive: The Specification, Extensions, and Custom Protocol Bindings"**,
Luca Muscariello and Darrel Miller, AGNTCon + MCPCon North America, Thursday October 22, 11:10–11:35 AM, Room 210B.

## Scenario

A buyer agent (GitHub Copilot driving the official `a2a` CLI) buys wine from Luca's supplier agent:

1. Discovery through an AI Catalog entry.
2. Natural-language quote over JSON-RPC.
3. Purchase with an OID4VP age check presented from a phone wallet.
4. Structured wine RFQ, quote and purchase order over JSON-RPC, aiming to demonstrate a transaction without LLM calls on either agent.
5. The same order over SLIMRPC, with the supplier off the public internet.

## Layout

- `docs/outline.md`: talk outline and timings.
- `docs/proposal-for-luca.md`: the plan as shared with Luca.
- `docs/windows-setup.md`: installing the A2A CLI, its SLIMRPC transport plugin and `slimctl` on Windows, and sending requests to the demo over SLIM.
- `catalog.json`: local AI Catalog pointing to live buyer and supplier Agent Cards; the buyer discovers suppliers from this catalog.

The buyer uses seller streaming when advertised. Wallet approval completes the
seller's immutable pending purchase and updates the original buyer task without
another message or LLM call. The dashboard observes the existing stream through
its local capture/polling view, not a second seller subscription.
- `stage-map/index.html`: offline HTML slide deck, starting with the session title slide. Open in a browser; arrow keys navigate, and `F` toggles fullscreen.
- `tools/credentials/`: local demo age-credential signing; phone-wallet issuance is a separate step.
- `BuyerAgent/`: C# A2A v1 buyer service with a GitHub Copilot SDK harness.
- `BuyerAgent.Tests/`: buyer protocol and supplier-tool tests.
- `SupplierAgent/`: Rust A2A v1 supplier using the official A2A and Copilot Rust SDKs; demo quotes and simulated orders, including deterministic typed profiles.
- `IssuerAgent/`: demo OpenID4VCI issuer for the phone wallet.
- `deployment/Deploy-Issuer.ps1`: validate and redeploy the issuer to Azure App Service.

## Buyer implementation

GitHub Copilot CLI drives the official `a2a` CLI to call the local `BuyerAgent`
service. Text input uses the Copilot SDK for its agent loop and GitHub-authenticated
inference; optional native wine-v1 typed input executes without an LLM. The official .NET A2A SDK handles inbound requests and outbound supplier
messages. Suppliers are found by searching an AI Catalog (default `catalog.json`)
and selected by catalog identifier, not configured as a fixed URL. Microsoft Agent Framework is not needed for this
initial implementation.

See [BuyerAgent setup and usage](BuyerAgent/README.md).

See [SupplierAgent setup, agreed schemas and limits](SupplierAgent/README.md).
The local supplier gates all purchases on task-bound OID4VP wallet verification
through the demo issuer. Real purchasing/payment/fulfillment are not implemented.
The buyer and Rust supplier also support an optional SLIMRPC binding; regular
HTTP JSON-RPC remains the default for the initial demo. Buyer and supplier
dashboard checkboxes control native input support and structured advertisement
(seller structures are disabled by default);
outbound format negotiation is automatic.

## Start the local demo on Windows

From the repository root in PowerShell:

```powershell
.\start-demo.ps1
```

This builds and starts the three local suppliers and buyer, opens the buyer's
`/demo` dashboard, and starts/reuses the Aspire dashboard through Podman. The
default transport is **HTTP JSON-RPC**. A stopped Podman machine is started
automatically when traces are enabled. Use `-NoTraces` to skip Podman/Aspire,
`-NoBrowser` to suppress browser tabs, or `-CheckOnly -NoTraces` to validate
the configuration and ports without building or starting services. Ctrl+C
stops the agent processes; the Aspire container remains running, matching the
Bash launcher. Add `-HttpBinding http-json` (or set `HTTP_BINDING=http-json`)
to make the buyer call suppliers over the A2A **HTTP+JSON** (REST) binding at
`/a2a/rest` instead; suppliers advertise and serve both HTTP bindings.

Equivalent environment variables from `start-demo.sh` and PowerShell options:

| Environment variable | PowerShell option | Default |
| --- | --- | --- |
| `COPILOT_GH_USER` | `-CopilotGhUser` | `lumuscar_cisco` |
| `COPILOT_MODEL` | `-CopilotModel` | `gpt-6-luna` |
| `BAROLO_INVENTORY` | `-BaroloInventory` | `SupplierAgent\inventories\barolo.json` |
| `SARDINIA_INVENTORY` | `-SardiniaInventory` | `SupplierAgent\inventories\sardinia.json` |
| `FRANCE_INVENTORY` | `-FranceInventory` | `SupplierAgent\inventories\france.json` |
| `SUPPLIER_VERIFIER_URL` | `-SupplierVerifierUrl` | unset |
| `SUPPLIER_VERIFIER_TOKEN` | `-SupplierVerifierToken` | unset; PowerShell also accepts `ISSUER_ADMIN_TOKEN` as a fallback |
| `TRACES=0` | `-NoTraces` | Aspire traces enabled |
| `SLIM` | `-Slim` | unset; `public` or `local` opts into SLIMRPC |
| `SLIM_NAMESPACE` | `-SlimNamespace` | `agentcon` |
| `SLIM_IDENTITY` | `-SlimIdentity` | `shared-secret`; optionally `did-key` |

Other script options: `-BuyerArgs` passes additional arguments through to the
buyer, for example
`.\start-demo.ps1 -BuyerArgs @('--Buyer:NativeStructuredSupport=true')`.
The positional arguments accepted by the Bash script are likewise additional
buyer command-line arguments. For secrets, set the verifier token in the current
process environment rather than putting it on the command line. PowerShell
reads `SUPPLIER_VERIFIER_TOKEN`, falling back to `ISSUER_ADMIN_TOKEN`; an explicit
`-SupplierVerifierToken` takes precedence. It does not prompt, and fails if a
verifier URL is supplied without a token.

The launcher requires .NET 10, Rust/MSVC build tools and matching-host libclang
(see [supplier Windows setup](SupplierAgent/README.md#run-on-this-windows-arm64-machine)), `gh` authenticated as the
selected Copilot account, and Podman with a Podman machine when traces are
enabled. Optional SLIM modes additionally require the tools and login described
in [Windows SLIM setup](docs/windows-setup.md). Logs are written under
`artifacts\demo-logs`; supplier Copilot state is kept outside the repository
under `%LOCALAPPDATA%\AgentConDemo`.
Rust build output defaults to `%LOCALAPPDATA%\AgentConDemo\rust\<worktree-hash>`
to avoid MSBuild long-path failures. Set `CARGO_TARGET_DIR` in the current
terminal to override it; the launcher runs the supplier from that directory.

### Compare HTTP with local SLIMRPC

Use the same request in both runs so the exchange dashboard shows the transport
difference, not a different task:

```powershell
# First run: ordinary HTTP JSON-RPC
.\start-demo.ps1 -NoTraces
a2a send -a http://localhost:5100 --timeout 150s --context-id http-demo `
  'Use send_to_all_suppliers to ask every catalog supplier for a quote for one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

Stop the first run with Ctrl+C, then start all four agents on a local SLIM node
and send the same request over SLIMRPC:

```powershell
.\start-demo.ps1 -Slim local -NoTraces
$env:A2A_SLIMRPC_PLUGIN_CONFIG = "$env:LOCALAPPDATA\AgentConDemo\slim\slim-client.json"
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s --context-id slim-demo `
  'Use send_to_all_suppliers to ask every catalog supplier for a quote for one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

In the buyer's exchange dashboard, compare **HTTP fan-out** (one request per
supplier, then task polling) with **SLIM multicast** (one group `SendMessage`
reaching all three registered suppliers, with their replies). SLIM suppliers
connect outbound to the node, so they do not need a SLIM listener exposed to
the buyer. This all-local run demonstrates the transport and group-routing
behavior; it does not by itself prove lower latency, internet-scale reachability,
or production-grade identity/security.
