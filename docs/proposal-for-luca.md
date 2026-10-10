# AgentCon A2A v1 talk: demo plan

**Format:** Demo-heavy. We build one agent-to-agent interaction live and introduce each spec concept when the demo needs it. No upfront spec deck.

**Scenario:** Buyer and supplier. My agent (A2A client) buys; your agent (A2A server) sells products. Each runs on our own laptop.

**Flow (~23 of 25 minutes)**

- **Discovery (3 min):** My agent finds your supplier agent through an AI Catalog entry and reads its Agent Card. *Introduces: A2A layers, Agent Cards, federated discovery.*
- **Natural-language quote and purchase over JSON-RPC (5 min):** My agent asks for a quote in plain language, judges the price and asks to buy. Your agent moves the task to `TASK_STATE_AUTH_REQUIRED` and requests proof of buying authority using the [OID4VP In-Task Authorization extension](https://github.com/a2aproject/experimental-ext-oid4vp-auth) (experimental). I scan a QR code and present the credential from a phone wallet; the task resumes. Capture timing as the baseline. *Introduces: extensions, task lifecycle.*
- **Structured quote and order over JSON-RPC (4 min):** Your card advertises a structured quote/order format (e.g. a UBL subset). My agent switches to it, and the accepted quote becomes a purchase order artifact. Transactions get faster because neither side needs an LLM to interpret prose. *Introduces: profiles.* Stretch: you change the format live and my agent adapts.
- **Same transactions over SLIMRPC (6 min):** Same app logic, different binding. Laptops on separate networks, both connecting out to a SLIM routing node; neither agent has a public endpoint. *Introduces: custom bindings, end-to-end encryption.*
- **Wrap-up (3 min):** When something should be an extension, a binding, or core spec.

**Positioning:** Structured messages bring the speed; SLIM brings private endpoints. We show measured SLIM numbers, not assumptions.

**Questions for you**

- Which OID4VP 1.0 wallet and credential issuer should we use, and who runs the verifier?
- Can we use an AGNTCY-hosted SLIM routing node? If not, I'll run one on a cloud VM.
- I'll add the SLIMRPC binding to the A2A SDK my client uses. Is there a reference implementation I should test against?
- Can a Catalog entry advertise a SLIM name today?
- I build the buyer, Catalog and card pieces; you build the supplier agent and SLIM setup?

**Fallback:** Pre-recorded run in case the conference network or wallet flow fails.
