# AgentCon San Jose 2026: A2A v1 Deep Dive

Demo materials for **"A2A v1 Deep Dive: The Specification, Extensions, and Custom Protocol Bindings"**,
Luca Muscariello and Darrel Miller, AGNTCon + MCPCon North America, Thursday October 22, 11:10–11:35 AM, Room 210B.

## Scenario

A buyer agent (GitHub Copilot driving the official `a2a` CLI) buys wine from Luca's supplier agent:

1. Discovery through an AI Catalog entry.
2. Natural-language quote over JSON-RPC.
3. Purchase with an OID4VP age check presented from a phone wallet.
4. Structured order over JSON-RPC.
5. The same order over SLIMRPC, with the supplier off the public internet.

## Layout

- `docs/outline.md`: talk outline and timings.
- `docs/proposal-for-luca.md`: the plan as shared with Luca.
- `stage-map/`: HTML interstitial diagrams used between demo segments.
