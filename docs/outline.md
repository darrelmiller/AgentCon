# AgentCon A2A v1 talk: outline (wine scenario)

23 minutes, leaving 2 minutes of buffer or questions.

## 0:00–2:00 Cold open *(both)*

- Darrel types into GitHub Copilot: "I'm hosting a dinner party. Find me a supplier who sells 2019 Barolo."
- Promise: "In 20 minutes Luca's supplier agent will come off the public internet, and my agent will still be able to buy from it. That's what a custom protocol binding gets you, and you don't have to change the spec to get it."
- One-line introductions, then straight into the demo.

## 2:00–5:00 Discovery *(Darrel drives, Luca narrates his card)*

- Buyer agent searches the AI Catalog and finds Luca's supplier agent.
- Show its Agent Card: skills, interface, and an extension declaration nobody explains yet.
- Interstitial 2: A2A's three layers (data model, operations, bindings).

## 5:00–8:00 Natural-language quote *(Darrel)*

- "Quote me 12 cases delivered to San Jose."
- Supplier agent replies in prose; buyer agent judges whether the price is reasonable.
- Show the task, messages and parts. Start a visible timer and token counter.

## 8:00–11:00 Purchase and age check *(both)*

- "Buy it." Task goes to `TASK_STATE_AUTH_REQUIRED`.
- QR code appears; Darrel scans it with his phone wallet.
- Luca shows his verifier received "over 21" and nothing else.
- Task resumes and completes.
- Explain the extension from the card: purpose, declaration, where its data goes.

## 11:00–15:00 Structured order *(Luca prompts, Darrel adapts)*

- Luca: "My agent prefers the structured order format."
- Buyer agent reorders with a structured data part; purchase order returns as an artifact.
- Side-by-side comparison with the natural-language run.
- Stretch: Luca adds a field live ("vintage substitution allowed") and the buyer adapts.
- Concepts: profiles, artifacts.

## 15:00–20:00 Private endpoints over SLIM *(Luca, then Darrel)*

- Set up the why: a public HTTP endpoint is attack surface, and a small supplier doesn't want to run ingress, a gateway and certificates just to take orders. A custom binding takes the endpoint off the public internet.
- Luca moves to a phone hotspot and shows there's no public IP or open port.
- His card lists a SLIMRPC interface; Catalog entry advertises a SLIM name.
- Same order. App logic unchanged, binding different.
- Show the routing node forwarding encrypted traffic it can't read.
- Concepts: custom bindings, why the layers matter.

## 20:00–23:00 Wrap-up *(both)*

- Framework: extension vs binding vs core spec, mapped onto each demo step.
- Links to the experimental repos; ask for feedback.

## Interstitials (HTML, no slide deck)

One "stage map" page: a diagram of buyer, AI Catalog, supplier, wallet and network that lights up the part we're about to demo. Arrow keys step through the states; each state is up for 20–40 seconds, then back to the demo.

1. **Cold open:** the whole map greyed out, with the promise as a caption.
2. **Discovery:** buyer → AI Catalog → Agent Card. Side panel: A2A's three layers.
3. **Age check:** task lifecycle with the `auth-required` detour through wallet and verifier. Shows only "over 21" crossing the wire.
4. **Structured order:** natural-language versus structured run, with the measured time and tokens loaded from the demo's own log.
5. **Off the internet:** before (public HTTP endpoint, ingress, certificates) versus after (both laptops dialling out to a SLIM routing node, encrypted end to end).
6. **Wrap-up:** extension versus binding versus core spec, with each demo step placed in its box.

Switching: the Copilot app shows the chat on the left and the map in a browser side panel, so nobody leaves the screen. If we use Copilot CLI instead, put the terminal and browser side by side.

## Fallback cues

- Wallet flow fails: play the recording.
- SLIM fails: play the recording.
- Never debug on stage.

## Open decisions

- Buyer agent: GitHub Copilot (CLI or app) driving the official [`a2a` CLI](https://github.com/a2aproject/a2a-cli) via its Agent Skill.
  - Already there: `--data-part` for structured orders; `--wait` stops on `auth-required`; pluggable transports (`a2a-transport-<name>` on PATH).
  - SLIM: a SLIMRPC plugin was built in a2a-rs (a2aproject/a2a-rs#296, closed); docs still open (a2aproject/a2a-rs#297). Verify it round-trips before building our own.
  - Gaps: no AI Catalog resolution (optional in the CLI spec, not implemented); no OID4VP wallet hand-off, so something must surface the QR code; extension opt-in probably via `--svc-param`, to confirm.
