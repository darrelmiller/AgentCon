#!/usr/bin/env bash
# Start the AgentCon demo on macOS: three Rust suppliers (Barolo :6000, Sardinian :6100, French :6200)
# + .NET buyer (:5100) with the /demo dashboard. Each supplier sells the products in its inventory JSON
# (SupplierAgent/inventories/); override one with BAROLO_INVENTORY, SARDINIA_INVENTORY or FRANCE_INVENTORY.
#
# Copilot inference uses the gh account named by COPILOT_GH_USER (default lumuscar_cisco),
# passed to every agent as COPILOT_GITHUB_TOKEN so the active gh account is left untouched.
#
# Optional wallet verification: set SUPPLIER_VERIFIER_URL (e.g. https://agentcon-issuer.azurewebsites.net);
# the script then prompts for SUPPLIER_VERIFIER_TOKEN (the issuer's ISSUER_ADMIN_TOKEN) unless it is
# already exported.
#
# Traces: starts (or reuses) a local Aspire dashboard container, agentcon-dashboard, and exports
# every agent's OTLP traces to it; UI at http://localhost:18888 (the /demo trace links open it).
# Set TRACES=0 to skip. The dashboard keeps running after Ctrl+C
# (stop it with: docker rm -f agentcon-dashboard).
#
# SLIMRPC: SLIM=public registers every agent on the AgentCon SLIM node (oidc.slim.outshift.io, GitHub or
# Cisco SSO via `slimctl login`); SLIM=local starts a local node with slimctl. Agents only dial out; the a2a CLI
# reaches them with --transport slimrpc using the config this script writes. SLIM_NAMESPACE overrides
# agntcy/<namespace>/<agent> (default agentcon). Unset SLIM keeps the HTTP-only demo.
# SLIM_IDENTITY=did-key gives every agent (suppliers, buyer, CLI) its own Ed25519 key (did:key, EdDSA
# JWT credentials verified against a JWKS of all demo agents) instead of one shared secret.
#
# Ctrl+C stops all agents. Logs go to ./artifacts/demo-logs/.
set -euo pipefail

cd "$(dirname "$0")"

GH_USER="${COPILOT_GH_USER:-lumuscar_cisco}"
# Copilot model for every agent: a lightweight model that follows the buyer's purchase rules reliably
# (gpt-5-mini skipped required steps in testing). Use "auto" to let Copilot pick.
MODEL="${COPILOT_MODEL:-gpt-6-luna}"
STATE_ROOT="$HOME/Library/Application Support/AgentConDemo"
LOG_DIR="artifacts/demo-logs"
INVENTORIES="$PWD/SupplierAgent/inventories"
# One supplier per row: label | port | inventory JSON | name for its log file and Copilot state | SLIM app.
# Ports must match catalog.json.
SUPPLIERS=(
    "Barolo supplier|6000|${BAROLO_INVENTORY:-$INVENTORIES/barolo.json}|supplier|barolo-supplier"
    "Sardinian supplier|6100|${SARDINIA_INVENTORY:-$INVENTORIES/sardinia.json}|sardinia|sardinian-supplier"
    "French supplier|6200|${FRANCE_INVENTORY:-$INVENTORIES/france.json}|france|french-supplier"
)
BUYER_PORT=5100

ports=("$BUYER_PORT")
for row in "${SUPPLIERS[@]}"; do
    IFS='|' read -r _ port inventory _ <<<"$row"
    ports+=("$port")
    [[ -f "$inventory" ]] || { echo "Inventory file not found: $inventory" >&2; exit 1; }
done
for port in "${ports[@]}"; do
    if lsof -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
        echo "Port $port is already in use:" >&2
        lsof -iTCP:"$port" -sTCP:LISTEN >&2
        echo "Stop that process first; this script will not kill it." >&2
        exit 1
    fi
done

# With only the verifier URL set, ask for the issuer admin token without echoing it.
if [[ -n "${SUPPLIER_VERIFIER_URL:-}" && -z "${SUPPLIER_VERIFIER_TOKEN:-}" && -t 0 ]]; then
    read -rsp "Issuer admin token for $SUPPLIER_VERIFIER_URL: " SUPPLIER_VERIFIER_TOKEN
    echo
    export SUPPLIER_VERIFIER_URL SUPPLIER_VERIFIER_TOKEN
fi
if [[ -n "${SUPPLIER_VERIFIER_URL:-}" && -z "${SUPPLIER_VERIFIER_TOKEN:-}" ]] ||
   [[ -z "${SUPPLIER_VERIFIER_URL:-}" && -n "${SUPPLIER_VERIFIER_TOKEN:-}" ]]; then
    echo "Set both SUPPLIER_VERIFIER_URL and SUPPLIER_VERIFIER_TOKEN, or neither." >&2
    exit 1
fi

if ! COPILOT_GITHUB_TOKEN="$(gh auth token --user "$GH_USER" 2>/dev/null)"; then
    echo "No gh token for $GH_USER. Run: gh auth login (as $GH_USER)" >&2
    exit 1
fi
export COPILOT_GITHUB_TOKEN

# Process-scoped control token for the dashboard's supplier "Advertise structures" toggle.
CONTROL_TOKEN="$(openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n')"

DASHBOARD_IMAGE="mcr.microsoft.com/dotnet/aspire-dashboard:9.5"
TRACES_UI="http://localhost:18888"
if [[ "${TRACES:-1}" != "0" ]]; then
    if ! docker info >/dev/null 2>&1; then
        echo "Docker is not running; start Docker Desktop or run with TRACES=0." >&2
        exit 1
    fi
    if [[ "$(docker inspect -f '{{.State.Running}}' agentcon-dashboard 2>/dev/null)" == "true" ]]; then
        echo "Reusing running Aspire dashboard container agentcon-dashboard."
    else
        docker rm -f agentcon-dashboard >/dev/null 2>&1 || true
        echo "Starting Aspire dashboard ($DASHBOARD_IMAGE)..."
        # Container 18889 is OTLP gRPC, 18890 is OTLP HTTP; see docs/telemetry.md.
        docker run -d --name agentcon-dashboard \
            -p 127.0.0.1:18888:18888 -p 127.0.0.1:4317:18889 -p 127.0.0.1:4318:18890 \
            "$DASHBOARD_IMAGE" >/dev/null
    fi
    # The UI requires the token-bearing login URL the container prints at startup.
    TRACES_LOGIN=""
    for _ in $(seq 1 20); do
        TRACES_LOGIN="$(docker logs agentcon-dashboard 2>&1 | grep -oE 'http://[^ ]*login\?t=[A-Za-z0-9]+' | tail -1 || true)"
        [[ -n "$TRACES_LOGIN" ]] && break
        sleep 1
    done
    export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
    export OTEL_EXPORTER_OTLP_PROTOCOL="grpc"
fi

SLIM_MODE="${SLIM:-}"
SLIM_DIR="$STATE_ROOT/slim"
SLIM_CONFIG="$SLIM_DIR/slim-client.json"
SLIM_ORG_NS="agntcy/${SLIM_NAMESPACE:-agentcon}"
if [[ -n "$SLIM_MODE" ]]; then
    case "$SLIM_MODE" in
        public)
            SLIM_TOKEN="$HOME/.slimctl/token"
            [[ -s "$SLIM_TOKEN" ]] || { echo "No slimctl token. Run: slimctl login --client-id slimctl --discovery-uri https://dev.idp.ads.outshift.io/.well-known/openid-configuration" >&2; exit 1; }
            # Agents re-read the token file every 5 minutes; re-run slimctl login before it expires (about 1 hour).
            if ! python3 -I -c 'import sys,json,base64,time; p=open(sys.argv[1]).read().strip().split(".")[1]; sys.exit(0 if json.loads(base64.urlsafe_b64decode(p+"="*(-len(p)%4)))["exp"]-time.time()>300 else 1)' "$SLIM_TOKEN" 2>/dev/null; then
                echo "The slimctl token expires in under 5 minutes (or is unreadable). Run slimctl login again." >&2; exit 1
            fi
            SLIM_CLIENT='{"endpoint":"https://oidc.slim.outshift.io:443","tls":{"insecure":false,"include_system_ca_certs_pool":true},"auth":{"type":"static_jwt","file":"'"$SLIM_TOKEN"'","duration":"5m"}}'
            ;;
        local)
            mkdir -p "$SLIM_DIR"
            if ! lsof -iTCP:46357 -sTCP:LISTEN >/dev/null 2>&1; then
                cat >"$SLIM_DIR/node.yaml" <<'YAML'
# Local SLIM data-plane node for the demo: loopback, plaintext.
runtime: { n_cores: 0, thread_name: "slim-data-plane", drain_timeout: 10s }
services:
  slim/0:
    dataplane:
      servers:
        - endpoint: "127.0.0.1:46357"
          tls: { insecure: true }
      clients: []
YAML
                echo "Starting local SLIM node (slimctl)..."
                slimctl slim start --config "$SLIM_DIR/node.yaml" >"$LOG_DIR/slim-node.log" 2>&1 &
                SLIM_NODE_PID=$!
                for _ in $(seq 1 20); do lsof -iTCP:46357 -sTCP:LISTEN >/dev/null 2>&1 && break; sleep 1; done
            fi
            SLIM_CLIENT='{"endpoint":"http://127.0.0.1:46357","tls":{"insecure":true}}'
            ;;
        *) echo "SLIM must be public or local." >&2; exit 1 ;;
    esac
    mkdir -p "$SLIM_DIR" && chmod 700 "$SLIM_DIR"
    # One shared secret for the demo apps' SLIM identity, kept outside the repository and reused across runs.
    [[ -s "$SLIM_DIR/shared-secret" ]] || (umask 077; openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n' >"$SLIM_DIR/shared-secret")
    SLIM_SHARED_SECRET="$(cat "$SLIM_DIR/shared-secret")"
    case "${SLIM_IDENTITY:-shared-secret}" in
        shared-secret)
            # One file for every SLIM client: suppliers and buyer read "client"; the a2a CLI plugin also reads "app".
            (umask 077; python3 -I - "$SLIM_CONFIG" "$SLIM_CLIENT" "$SLIM_ORG_NS/cli" "$SLIM_SHARED_SECRET" <<'PY'
import json, sys
path, client, name, secret = sys.argv[1:]
identity = {"type": "shared_secret", "id": name, "data": secret}
json.dump({"client": json.loads(client), "app": {"name": name, "identity_provider": identity, "identity_verifier": identity}},
          open(path, "w"), indent=2)
PY
            )
            ;;
        did-key)
            # One config per agent (own key, shared trust list); the CLI's becomes the plugin config.
            echo "SLIM identities (did:key):"
            python3 -I tools/slim/slim_identities.py "$SLIM_DIR" "$SLIM_CLIENT" "$SLIM_ORG_NS" \
                cli barolo-supplier sardinian-supplier french-supplier buyer | sed 's/^/  /'
            (umask 077; cp "$SLIM_DIR/cli.json" "$SLIM_CONFIG")
            ;;
        *) echo "SLIM_IDENTITY must be shared-secret or did-key." >&2; exit 1 ;;
    esac
fi

echo "Building suppliers and buyer..."
cargo build --manifest-path SupplierAgent/Cargo.toml --locked --quiet
dotnet build BuyerAgent/BuyerAgent.csproj --nologo -v quiet >/dev/null

mkdir -p "$LOG_DIR"

pids=()
cleanup() {
    trap - INT TERM EXIT
    echo
    echo "Stopping agents..."
    for pid in ${pids[@]+"${pids[@]}"}; do
        kill "$pid" 2>/dev/null || true
    done
    wait 2>/dev/null || true
}
trap cleanup INT TERM EXIT

for row in "${SUPPLIERS[@]}"; do
    IFS='|' read -r _ port inventory name slim_app <<<"$row"
    mkdir -p "$STATE_ROOT/$name-copilot"
    if [[ -n "$SLIM_MODE" ]]; then
        export SUPPLIER_SLIM_NAME="$SLIM_ORG_NS/$slim_app" SLIM_SHARED_SECRET
        if [[ "${SLIM_IDENTITY:-shared-secret}" == did-key ]]; then
            export SUPPLIER_SLIM_CONFIG="$SLIM_DIR/$slim_app.json"   # its own key and the trust list
        else
            export SUPPLIER_SLIM_CONFIG="$SLIM_CONFIG"
        fi
    fi
    SUPPLIER_PORT="$port" \
    SUPPLIER_INVENTORY="$inventory" \
    SUPPLIER_MODEL="$MODEL" \
    SUPPLIER_COPILOT_STATE="$STATE_ROOT/$name-copilot" \
    SUPPLIER_DEMO_CONTROL_TOKEN="$CONTROL_TOKEN" \
        ./SupplierAgent/target/debug/agentcon-supplier >"$LOG_DIR/$name.log" 2>&1 &
    pids+=($!)
done

buyer_slim=()
if [[ -n "$SLIM_MODE" ]]; then
    if [[ "${SLIM_IDENTITY:-shared-secret}" == did-key ]]; then
        buyer_slim=(--Buyer:SlimConfigPath="$SLIM_DIR/buyer.json" --Buyer:SlimName="$SLIM_ORG_NS/buyer")
    else
        buyer_slim=(--Buyer:SlimConfigPath="$SLIM_CONFIG" --Buyer:SlimName="$SLIM_ORG_NS/buyer")
    fi
fi
Buyer__SlimSharedSecret="${SLIM_SHARED_SECRET:-}" \
Buyer__SupplierControlToken="$CONTROL_TOKEN" \
    dotnet run --no-build --project BuyerAgent/BuyerAgent.csproj -- \
    --Buyer:ExchangeCaptureEnabled=true --Buyer:Model="$MODEL" ${buyer_slim[@]+"${buyer_slim[@]}"} "$@" >"$LOG_DIR/buyer.log" 2>&1 &
pids+=($!)
[[ -n "${SLIM_NODE_PID:-}" ]] && pids+=("$SLIM_NODE_PID")

wait_healthy() {
    local name="$1" url="$2" log="$3"
    for _ in $(seq 1 60); do
        curl -sf "$url" >/dev/null && { echo "  $name ready: $url"; return 0; }
        sleep 1
    done
    echo "$name did not become healthy; last log lines:" >&2
    tail -20 "$log" >&2
    exit 1
}

echo "Starting agents..."
for row in "${SUPPLIERS[@]}"; do
    IFS='|' read -r label port _ name <<<"$row"
    wait_healthy "$label" "http://localhost:$port/health" "$LOG_DIR/$name.log"
done
wait_healthy buyer "http://localhost:$BUYER_PORT/health" "$LOG_DIR/buyer.log"

[[ -n "${SUPPLIER_VERIFIER_URL:-}" ]] || echo "  (wallet verifier not configured: purchases will stop at auth-required)"

open "http://localhost:$BUYER_PORT/demo"
[[ "${TRACES:-1}" != "0" ]] && open "${TRACES_LOGIN:-$TRACES_UI}"

cat <<EOF

Model:     $MODEL (override with COPILOT_MODEL=...)
Dashboard: http://localhost:$BUYER_PORT/demo
Traces:   $([[ "${TRACES:-1}" != "0" ]] && echo "${TRACES_LOGIN:-$TRACES_UI (login URL: docker logs agentcon-dashboard)}" || echo "disabled (TRACES=0)")
Logs:      $LOG_DIR/

Try:
  a2a send -a http://localhost:$BUYER_PORT --timeout 150s --context-id dinner 'Search the catalog for wine suppliers, select urn:agentcon:agent:demo-wine-supplier, and ask for a demo quote for 12 cases of 2019 Barolo delivered to San Jose. Do not purchase.'
  a2a send -a http://localhost:$BUYER_PORT --timeout 150s --context-id dinner 'Buy the quoted wine as a simulated demo order only.'
  a2a send -a http://localhost:$BUYER_PORT --timeout 150s --context-id sardinia 'Search the catalog for a supplier of Vermentino and quote one case of 2020 Vermentino from Arzachena delivered to San Jose, US. Do not purchase.'
  a2a send -a http://localhost:$BUYER_PORT --timeout 150s --context-id france 'Search the catalog for a French supplier and quote one case of 2015 Château Margaux and one case of Dom Ruinart Champagne delivered to San Jose, US. Do not purchase.'

EOF
if [[ -n "$SLIM_MODE" ]]; then
    cat <<EOF
SLIMRPC ($SLIM_MODE, identity ${SLIM_IDENTITY:-shared-secret}): agents registered as slim://$SLIM_ORG_NS/{buyer,barolo-supplier,sardinian-supplier,french-supplier}
  export A2A_SLIMRPC_PLUGIN_CONFIG="$SLIM_CONFIG"
EOF
    echo "  a2a send -e slim://$SLIM_ORG_NS/buyer --transport slimrpc --timeout 150s --context-id slim 'Search the catalog for wine suppliers and quote 12 cases of 2019 Barolo to San Jose, US. Do not purchase.'"
    echo
fi
echo "Press Ctrl+C to stop all agents."

# Portable "wait for any agent to exit" (macOS ships bash 3.2, which lacks wait -n).
all_running() {
    for pid in "${pids[@]}"; do kill -0 "$pid" 2>/dev/null || return 1; done
}
while all_running; do
    sleep 2
done
echo "An agent exited; see $LOG_DIR/." >&2
