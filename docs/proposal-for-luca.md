# AgentCon A2A v1 talk: demo plan

**Format:** Demo-heavy. We build one agent-to-agent interaction live and introduce each spec concept when the demo needs it. No upfront spec deck.

**Scenario:** Buyer and supplier. My agent (A2A client) buys; your agent (A2A server) sells products. Each runs on our own laptop.

**Flow (~23 of 25 minutes)**

- **Discovery (3 min):** My agent finds your supplier agent through an AI Catalog entry and reads its Agent Card. *Introduces: A2A layers, Agent Cards, federated discovery.*
- **Natural-language quote and purchase over JSON-RPC (5 min):** My agent asks for a quote in plain language, judges the price and asks to buy. Your agent moves the task to `TASK_STATE_AUTH_REQUIRED` and requests proof of buying authority using the [OID4VP In-Task Authorization extension](https://github.com/a2aproject/experimental-ext-oid4vp-auth) (experimental). I scan a QR code and present the credential from a phone wallet; the task resumes. Capture timing as the baseline. *Introduces: extensions, task lifecycle.*
- **Structured RFQ, quote and purchase order over JSON-RPC (4 min):** Your card advertises media types for wine RFQs, quotes and purchase orders, each with an agreed versioned schema. Deterministic code on both agents exchanges typed structured parts and returns the result as an artifact. The goal is to demonstrate a transaction with no LLM calls on either side, not just less prose interpretation. Measure elapsed time and inference calls/tokens for both runs; discovery/setup or LLM-assisted schema adaptation is outside the measured transaction. The current buyer's structured-input path still uses Copilot, so it needs a separate deterministic route. *Introduces: media types, profiles, artifacts.*
- **Same transactions over SLIMRPC (6 min):** Same app logic, different binding. Laptops on separate networks, both connecting out to a SLIM routing node; neither agent has a public endpoint. *Introduces: custom bindings, end-to-end encryption.*
- **Wrap-up (3 min):** When something should be an extension, a binding, or core spec.

**Positioning:** Agreed structured formats can let agents transact without an LLM; SLIM brings private endpoints. Any speed or zero-inference claim must be supported by measurements, not assumptions.

**Buyer implementation update:** GitHub Copilot CLI drives the official `a2a` CLI to call a local C# buyer service. That service uses the GitHub Copilot SDK as the harness and the official .NET A2A SDK for supplier calls. The initial service uses JSON-RPC; SLIMRPC and phone-wallet hand-off still need integration.

**Questions for you**

- Which OID4VP 1.0 wallet and credential issuer should we use, and who runs the verifier?
- Can we use an AGNTCY-hosted SLIM routing node? If not, I'll run one on a cloud VM.
- I'll add the SLIMRPC binding to the A2A SDK my client uses. Is there a reference implementation I should test against?
- Can a Catalog entry advertise a SLIM name today?
- I build the buyer, Catalog and card pieces; you build the supplier agent and SLIM setup?

**Fallback:** Pre-recorded run in case the conference network or wallet flow fails.
