# Demo runbook

How to run the AgentCon demo against the three wine sellers in the cloud, stage by stage. There are two runs, one per
transport, so the dashboard shows a single kind of message chip at a time:

| Run | a2a CLI → buyer | Buyer → sellers | Chips |
|---|---|---|---|
| **SLIM** | SLIMRPC (`slim://agntcy/agentcon/buyer`) | SLIM: one multicast to ask every seller | blue `SLIM` |
| **HTTPS** | HTTP (`http://localhost:5100`) | A2A JSON-RPC over HTTPS, through the OIDC gateway | green `HTTPS` |

Each run asks every seller, then Sardinia and France, then quotes and buys a Barolo, which the seller only completes
after an over-21 wallet proof. No payment or fulfillment ever happens.

Every stage below is one command block that starts by printing its banner, so the terminal shows where the demo is.
The two runs are told apart by their stage names and banner colours: the SLIM run's stages are **S3–S9**, in blue
(`== SLIM · Stage S4 · … ==`, like the blue SLIM chips), and the HTTPS run's are **H3–H9**, in green
(`== HTTPS · Stage H4 · … ==`, like the green HTTPS chips). Stages 0, 1, 2 and 10 are shared, in yellow. Run the blocks in order from the repository root, by hand or with Claude Code:

```bash
claude "Run the full SLIM demo from RUNBOOK.md"
claude "Run the full HTTPS demo from RUNBOOK.md"
```

## Stage 0 · Check the prerequisites

```bash
printf '\n\033[1;33m== Stage 0 · Check the prerequisites ==\033[0m\n'
python3 -c 'import json,base64,time,os; p=open(os.path.expanduser("~/.slimctl/token")).read().strip().split(".")[1]; c=json.loads(base64.urlsafe_b64decode(p+"=="*2)); left=int(c["exp"]-time.time()); print("SLIM login:", c.get("name"), "- expires in", left//60, "min" if left > 0 else "- EXPIRED")'
gh auth status 2>&1 | grep -E "Logged in to github.com account" | head -3
for tool in a2a a2a-transport-slimrpc slimctl dotnet cargo jq; do command -v "$tool" >/dev/null && echo "ok: $tool" || echo "MISSING: $tool"; done
npx -y --package=@agntcy/dir-mcp dirctl auth status 2>&1 | grep -E "Email|Expires"
docker info >/dev/null 2>&1 && echo "ok: Docker" || echo "MISSING: Docker Desktop is not running"
lsof -iTCP:5100 -sTCP:LISTEN >/dev/null 2>&1 && echo "port 5100 is in use (a buyer is running)" || echo "port 5100 is free"
```

| Check | Needed | Fix |
|---|---|---|
| SLIM login | your own account, at least 15 minutes left | `slimctl login --client-id slimctl --discovery-uri https://dev.idp.ads.outshift.io/.well-known/openid-configuration` |
| GitHub CLI | logged in as `COPILOT_GH_USER` (default `lumuscar_cisco`), with Copilot | `gh auth login` as that account |
| Tools | all `ok` | see [docs/windows-setup.md](docs/windows-setup.md) for the CLI and plugin |
| Directory login | an `Expires` in the future, on the `prod` context (`ads.outshift.io`) | `dirctl context set prod`, then `dirctl auth login` |
| Docker | `ok` (for the Aspire traces dashboard) | start Docker Desktop |
| Phone | a wallet holding the AgentCon over_21 credential | see the issuer's offer page (`IssuerAgent/README.md`) |

The SLIM login must belong to an account the gateway allows (`muscariello` or `darrelmiller`): a login as another
account starts the buyer without SLIM and gets HTTP 403 from the HTTPS gateway.

## Stage 1 · Find and verify the sellers in the Directory

The sellers are published in the AGNTCY Directory (`ads.outshift.io`) as OASF records, signed with the AgentCon cosign
key, whose public key is served next to the cards at <https://agents.slim.outshift.io/agentcon/cosign.pub>. This stage
lists the records, verifies each signature against that key, pulls the A2A card out of each record and checks that it
matches the card the seller serves.

```bash
printf '\n\033[1;33m== Stage 1 · Find and verify the sellers in the Directory ==\033[0m\n'
dirctl() { npx -y --package=@agntcy/dir-mcp dirctl "$@" </dev/null; }
dirctl search --name 'https://agents.slim.outshift.io/agentcon/*'
for seller in barolo-supplier sardinian-supplier french-supplier; do
  name="https://agents.slim.outshift.io/agentcon/$seller"
  cid=$(dirctl search --name "$name" --output raw)
  echo "-- $seller ($cid)"
  dirctl verify "$cid" --key https://agents.slim.outshift.io/agentcon/cosign.pub | grep -E "signature is|Reason"
  dirctl pull "$name" --output json \
    | jq '(.data // .) | .modules[] | select(.name == "integration/a2a") | .data.card_data' > "/tmp/$seller.card.json"
  jq -c '{name, interfaces: [.supportedInterfaces[].url]}' "/tmp/$seller.card.json"
  diff -q <(jq -S . "/tmp/$seller.card.json") <(curl -s "$name/agent-card.json" | jq -S .) >/dev/null \
    && echo "card matches the published card" || echo "card DIFFERS from the published card"
done
```

Expect, for each seller: `Record signature is: trusted`; the interfaces
`https://agents.slim.outshift.io/agentcon/<seller>/a2a` (JSON-RPC over HTTPS) and `slim://agntcy/agentcon/<seller>`
(SLIMRPC); and `card matches the published card`. A record that isn't trusted, or a card that differs, isn't the one
AgentCon signed: stop, and don't run the demo against it.

## Stage 2 · Start the buyer

Skip this stage when Stage 0 said port 5100 is in use and the buyer is on SLIM (the check at the end of this stage).
Otherwise start it in its own terminal, because it keeps running:

```bash
printf '\n\033[1;33m== Stage 2 · Start the buyer ==\033[0m\n'
SLIM=public SLIM_IDENTITY=did-web SELLERS=cloud ./start-demo.sh
```

Wait for `buyer ready: http://localhost:5100/health` and `Press Ctrl+C to stop all agents.` Only the buyer runs
locally; the sellers run in the slim-dev cluster. The script opens two pages:

- the exchange dashboard, <http://localhost:5100/demo>;
- the Aspire dashboard for the OpenTelemetry traces, <http://localhost:18888>, in the Docker container
  `agentcon-dashboard`. Its UI needs the token-bearing login URL the script prints on its `Traces:` line (also in
  `docker logs agentcon-dashboard`). The container keeps running after Ctrl+C (`docker rm -f agentcon-dashboard`
  stops it). Selecting a message on the exchange dashboard shows a **Trace** link in the message inspector, which
  opens its trace in Aspire. The traces come from the buyer; the cloud sellers don't export traces to this laptop.

Then check, in another terminal, that the buyer is registered on SLIM (a few seconds after "ready"):

```bash
printf '\n\033[1;33m== Stage 2 · Check the buyer is on SLIM ==\033[0m\n'
curl -s http://localhost:5100/demo/state | python3 -c 'import json,sys; print("buyer on SLIM:", json.load(sys.stdin)["buyer"]["slimUrl"])'
```

It prints `buyer on SLIM: slim://agntcy/agentcon/buyer`. `None` for more than a minute means the SLIM login was refused
(see Troubleshooting).

## The SLIM run

### Stage S3 · SLIM · Set the transport and clear the dashboard

```bash
printf '\n\033[1;34m== SLIM · Stage S3 · Set the transport and clear the dashboard ==\033[0m\n'
curl -s -H 'Origin: http://localhost:5100' -H 'Content-Type: application/json' \
  -X POST http://localhost:5100/demo/transport -d '{"transport":"Slim"}'; echo
curl -s -o /dev/null -w 'dashboard cleared: HTTP %{http_code}\n' -H 'Origin: http://localhost:5100' -X POST http://localhost:5100/demo/clear
```

The dashboard's conversation header now shows `Next request: SLIM`. Every message of this run carries a blue `SLIM`
chip, including the user agent's requests to the buyer.

### Stage S4 · SLIM · Ask every seller

```bash
printf '\n\033[1;34m== SLIM · Stage S4 · Ask every seller (one multicast to the three sellers) ==\033[0m\n'
export A2A_SLIMRPC_PLUGIN_CONFIG="$HOME/Library/Application Support/AgentConDemo/slim/slim-client.json"
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s --context-id slim-tasting \
  'Quote one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

One `SLIM · multicast` request reaches the three sellers, each answering in about 2 s. Barolo quotes; the other two
say they don't stock it.

### Stage S5 · SLIM · Sardinia

```bash
printf '\n\033[1;34m== SLIM · Stage S5 · Sardinia: the buyer picks the seller from the catalog ==\033[0m\n'
export A2A_SLIMRPC_PLUGIN_CONFIG="$HOME/Library/Application Support/AgentConDemo/slim/slim-client.json"
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s --context-id slim-sardinia \
  'Search the catalog for a supplier of Vermentino and quote one case of 2020 Vermentino from Arzachena delivered to San Jose, US. Do not purchase.'
```

### Stage S6 · SLIM · France, two wines

```bash
printf '\n\033[1;34m== SLIM · Stage S6 · France: two wines from one seller ==\033[0m\n'
export A2A_SLIMRPC_PLUGIN_CONFIG="$HOME/Library/Application Support/AgentConDemo/slim/slim-client.json"
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s --context-id slim-france \
  'Search the catalog for a French supplier and quote one case of 2015 Château Margaux and one case of Dom Ruinart Champagne delivered to San Jose, US. Do not purchase.'
```

### Stage S7 · SLIM · Quote the Barolo

```bash
printf '\n\033[1;34m== SLIM · Stage S7 · Quote the Barolo ==\033[0m\n'
export A2A_SLIMRPC_PLUGIN_CONFIG="$HOME/Library/Application Support/AgentConDemo/slim/slim-client.json"
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s --context-id slim-dinner \
  'Search the catalog for wine suppliers, select urn:agentcon:agent:demo-wine-supplier, and quote one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

### Stage S8 · SLIM · Buy the Barolo, with the age check

```bash
printf '\n\033[1;34m== SLIM · Stage S8 · Buy the Barolo: the seller asks for an over-21 wallet proof ==\033[0m\n'
export A2A_SLIMRPC_PLUGIN_CONFIG="$HOME/Library/Application Support/AgentConDemo/slim/slim-client.json"
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s --context-id slim-dinner \
  'Buy the quoted wine as a simulated demo order only.'
```

The reply is `Status: auth-required`, with a `Task:` ID and a link
`https://agentcon-issuer.azurewebsites.net/presentations/<id>`. Show the QR code, either:

- on the exchange dashboard: choose the **Barolo seller** in the Seller Agent panel ("Choose a catalog seller") and
  click **Show QR** on its auth-required task (the link is on the seller's task, because the seller asks for the
  proof); or
- by opening the presentation link in a browser.

Scan it with the wallet, choose the AgentCon over_21 credential and approve, within about 10 minutes.

### Stage S9 · SLIM · Check the order

With the `Task:` ID from Stage S8 in place of `<task-id>`:

```bash
printf '\n\033[1;34m== SLIM · Stage S9 · Check the order ==\033[0m\n'
export A2A_SLIMRPC_PLUGIN_CONFIG="$HOME/Library/Application Support/AgentConDemo/slim/slim-client.json"
a2a task get -e slim://agntcy/agentcon/buyer --transport slimrpc <task-id>
```

While the wallet hasn't approved yet it shows `auth-required`: run it again. After approval it shows
`Status: completed` and an `order-receipt` artifact with `"ageVerification":"verified-demo-over-21"`,
`"status":"simulated"` and `"payment":"not-performed"`.

## The HTTPS run

Same stages, over HTTP to the buyer and HTTPS from the buyer to the sellers (A2A JSON-RPC through the OIDC gateway,
with the SLIM login as bearer token). Use it after the SLIM run, or on its own.

### Stage H3 · HTTPS · Set the transport and clear the dashboard

```bash
printf '\n\033[1;32m== HTTPS · Stage H3 · Set the transport and clear the dashboard ==\033[0m\n'
curl -s -H 'Origin: http://localhost:5100' -H 'Content-Type: application/json' \
  -X POST http://localhost:5100/demo/transport -d '{"transport":"Http"}'; echo
curl -s -o /dev/null -w 'dashboard cleared: HTTP %{http_code}\n' -H 'Origin: http://localhost:5100' -X POST http://localhost:5100/demo/clear
```

The header shows `Next request: HTTPS`; every message of this run carries a green `HTTPS` chip (the user agent's
requests to the buyer show `HTTP`, the local hop).

### Stage H4 · HTTPS · Ask every seller

```bash
printf '\n\033[1;32m== HTTPS · Stage H4 · Ask every seller (one HTTPS request per seller) ==\033[0m\n'
a2a send -a http://localhost:5100 --timeout 150s --context-id https-tasting \
  'Quote one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

One request per seller, polled until each answers, instead of one multicast.

### Stage H5 · HTTPS · Sardinia

```bash
printf '\n\033[1;32m== HTTPS · Stage H5 · Sardinia: the buyer picks the seller from the catalog ==\033[0m\n'
a2a send -a http://localhost:5100 --timeout 150s --context-id https-sardinia \
  'Search the catalog for a supplier of Vermentino and quote one case of 2020 Vermentino from Arzachena delivered to San Jose, US. Do not purchase.'
```

### Stage H6 · HTTPS · France, two wines

```bash
printf '\n\033[1;32m== HTTPS · Stage H6 · France: two wines from one seller ==\033[0m\n'
a2a send -a http://localhost:5100 --timeout 150s --context-id https-france \
  'Search the catalog for a French supplier and quote one case of 2015 Château Margaux and one case of Dom Ruinart Champagne delivered to San Jose, US. Do not purchase.'
```

### Stage H7 · HTTPS · Quote the Barolo

```bash
printf '\n\033[1;32m== HTTPS · Stage H7 · Quote the Barolo ==\033[0m\n'
a2a send -a http://localhost:5100 --timeout 150s --context-id https-dinner \
  'Search the catalog for wine suppliers, select urn:agentcon:agent:demo-wine-supplier, and quote one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

### Stage H8 · HTTPS · Buy the Barolo, with the age check

```bash
printf '\n\033[1;32m== HTTPS · Stage H8 · Buy the Barolo: the seller asks for an over-21 wallet proof ==\033[0m\n'
a2a send -a http://localhost:5100 --timeout 150s --context-id https-dinner \
  'Buy the quoted wine as a simulated demo order only.'
```

Approve as in Stage S8: **Show QR** on the Barolo seller's task, or the presentation link.

### Stage H9 · HTTPS · Check the order

With the `Task:` ID from Stage H8 in place of `<task-id>`:

```bash
printf '\n\033[1;32m== HTTPS · Stage H9 · Check the order ==\033[0m\n'
a2a task get -a http://localhost:5100 <task-id>
```

## Stage 10 · Stop

```bash
printf '\n\033[1;33m== Stage 10 · Stop ==\033[0m\n'
lsof -tiTCP:5100 -sTCP:LISTEN | xargs kill 2>/dev/null; echo "buyer stopped"
```

Or press Ctrl+C in the terminal that runs `start-demo.sh`. The Aspire container keeps running for the next demo.

## Expected quotes

| Wine | Case | Delivery | Total |
|---|---|---|---|
| 2019 Barolo | USD 144.00 | USD 25.00 | USD 169.00 |
| 2020 Vermentino di Gallura Capichera | USD 540.00 | USD 40.00 | USD 580.00 |
| 2015 Château Margaux | USD 10,800.00 | USD 150.00 | USD 10,950.00 |
| 2013 Dom Ruinart Blanc de Blancs | USD 3,000.00 | USD 150.00 | USD 3,150.00 |

The prices are in each reply's `[quote]` artifact (`casePriceMinor`, `deliveryMinor` and `totalMinor`, in cents); the
status line can be as short as "Completed; results are in the task artifacts". Seller replies show `LLM calls: 0`:
the buyer sends typed (JSON Schema) requests, which the sellers price without their model.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `slimUrl` stays `None`; HTTPS calls get 403 | expired SLIM login, or a login as an account the gateway doesn't allow | `slimctl login` as your own account, then restart the buyer (Stage 2) |
| Calls fail or hang, buyer log shows SLIM reconnects | the SLIM login expired during the demo (logins last about an hour) | `slimctl login`, restart the buyer (Stage 2), continue from Stage S3 or H3 |
| `Jwt is expired` / 401 from the gateway | the SLIM login expired | `slimctl login`; the buyer re-reads the token file within 5 minutes |
| Seller says "Structured input is disabled; send text/plain" | the seller runs without `SUPPLIER_STRUCTURED_DATA=true` | the seller chart must be 0.1.5 or later (`structuredData: true`) |
| Dashboard group "SLIM multicast failed · 3 × SendMessage instead" | the multicast didn't complete within 20 s; the buyer asked each seller instead | SLIM 3.4.5 fixed the known cause (agntcy/slim#2325); keep the laptop clock synced (`sntp pool.ntp.org`) |
| `Wallet authorization expired` | no wallet approval within about 10 minutes | run Stages S7 and S8 (or H7 and H8) again with a new context ID for a new link |
| No **Show QR** link | the purchase hasn't reached auth-required yet, or a different seller is selected | wait for the reply of Stage S8 or H8; select the Barolo seller in the Seller Agent panel |
| `Record signature is: not trusted` | the record isn't signed with the AgentCon key, or was changed after signing | don't use it; re-sign it with the AgentCon key (`dirctl sign <cid> --key …`) |
| `dirctl` reports an auth or permission error | no Directory login, or the wrong context | `dirctl context set prod`, then `dirctl auth login` |
| `Docker is not running; start Docker Desktop or run with TRACES=0.` | Docker Desktop isn't running | start it, or run the buyer without traces (`TRACES=0`) |
| Aspire asks for a login token | the UI needs the token-bearing URL | the URL on the script's `Traces:` line, or `docker logs agentcon-dashboard` |
| `Port 5100 is already in use` | a buyer from an earlier run | reuse it (skip Stage 2), or stop it (Stage 10) |
| Every call is slow (10 s or more per multicast) | a high-latency network, such as in-flight Wi-Fi | it still works; record on a better network |

## For Claude Code

When asked to run the SLIM or the HTTPS demo:

- Go through the stages in order: 0, 1, 2 (only if no buyer is on SLIM yet), then S3 to S9 for the SLIM run or H3 to
  H9 for the HTTPS run. Before
  each stage, tell the user which stage it is, then run that stage's block exactly as written; it prints its own
  banner. Show the user the reply, and continue only when the stage succeeded. Report a failed stage; don't retry it
  blindly.
- Stage 2 starts a long-running process: run it in a Terminal tab the user can see (or ask the user to start it), not
  as a blocking command.
- Never print secrets. Stage 0 only decodes the `exp` and `name` claims of `~/.slimctl/token`; never print the token
  or put it in a command argument. If the SLIM login is expired, has less than 15 minutes left, or belongs to another
  account, stop and ask the user to run `slimctl login`.
- Run Stage S8 or H8 (the purchase) only when the user asked for the full demo. Then post the presentation link, point the
  user to **Show QR** on the Barolo seller's task in the dashboard's Seller Agent panel, and wait for them to approve.
  Never resend a purchase on your own: a second order could be created. Check the order with Stage S9 or H9 when the user
  says they approved, or every 30 seconds until it is no longer `auth-required`.
- Keep the same transport for the whole run, and the context IDs as written (use a new suffix only when repeating a
  run in the same buyer session).
