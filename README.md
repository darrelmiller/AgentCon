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
through the demo issuer. Real purchasing/payment/fulfillment and SLIMRPC remain
unimplemented. Buyer and supplier dashboard checkboxes control native input
support and structured advertisement; outbound format negotiation is automatic.
