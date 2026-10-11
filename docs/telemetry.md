# AgentCon OTLP tracing and Aspire

Both services export OpenTelemetry **traces** (not metrics or application logs).
For an opt-in payload viewer, see the buyer's
[live exchange dashboard](../BuyerAgent/README.md#live-exchange-dashboard).
It captures A2A wire bodies separately in local memory, never in trace attributes.
The viewer also shows `agentcon.llmCalls` message metadata, based on actual SDK
assistant-turn/retry events. These per-message counts are separate from token
usage (which remains sourced only from SDK usage events). Structured seller
replies report zero; the Copilot-powered buyer still uses inference.
Default resource `service.name` values are `agentcon-buyer` and
`agentcon-supplier`; `OTEL_SERVICE_NAME` overrides each independently.
`service.version` comes from the .NET assembly informational version and Rust
package version respectively.

## Start a standalone dashboard

The current [official standalone instructions](https://aspire.dev/dashboard/standalone/)
support a non-Docker Aspire CLI path:

```powershell
npx -y @microsoft/aspire-cli dashboard run
# Or with an installed Aspire CLI:
aspire dashboard run
```

UI defaults to `http://localhost:18888`, OTLP gRPC to `http://localhost:4317`,
OTLP HTTP to `http://localhost:4318`. Open the token-bearing login URL printed
by the CLI. Keep UI token authentication and loopback binding enabled. The
standalone CLI dashboard was run and verified on Windows ARM64 without Docker.
Its default OTLP receiver is unauthenticated (the UI displays a warning);
do not expose these ports to other machines. Tokens are ephemeral and must not
be committed.

Where Docker is available, the equivalent container mappings are:

```powershell
docker run --rm -it --name agentcon-dashboard `
  -p 127.0.0.1:18888:18888 -p 127.0.0.1:4317:18889 -p 127.0.0.1:4318:18890 `
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Container **18889 is gRPC**, **18890 is HTTP**; the mapped host ports above are
4317/4318. Do not send OTLP to the dashboard UI. The dashboard is a local
diagnostic viewer, not durable production retention.

## Configure each agent process

In each separate service terminal, before starting it:

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'
$env:OTEL_EXPORTER_OTLP_PROTOCOL = 'grpc'
# Optional authenticated collector (never put real keys in source/docs):
# $env:OTEL_EXPORTER_OTLP_HEADERS = 'x-otlp-api-key=<your-key>'
# Leave OTEL_SERVICE_NAME unset for distinct buyer/supplier defaults.
```

HTTP/protobuf is also supported:

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4318'
$env:OTEL_EXPORTER_OTLP_PROTOCOL = 'http/protobuf'
```

Use a **base endpoint**, without `/v1/traces`, for the generic HTTP setting.
Both SDK exporters support standard `OTEL_EXPORTER_OTLP_HEADERS`,
`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL`,
and `OTEL_EXPORTER_OTLP_TRACES_HEADERS` overrides. A signal-specific HTTP endpoint
is the complete URL, e.g. `http://localhost:4318/v1/traces`.
Supported common protocols are `grpc` (default) and `http/protobuf`, not
`http/json`. Clear stale signal-specific overrides before switching protocols.

Without a nonempty generic or trace-specific endpoint, the exporter is
**deliberately disabled** and startup prints this decision. Instrumentation and
W3C propagation remain enabled. Export failures are observable: .NET prints
OpenTelemetry warning/error EventSource names without sensitive payloads; Rust
SDK/exporter warnings go to stderr through the tracing subscriber. Invalid
exporter configuration is not silently ignored. Graceful application shutdown
flushes traces; forcibly terminating a process cannot guarantee delivery.

Start only new/approved service processes. To leave existing demo services at
5100/6000 untouched, create an isolated catalog for 5101/6001:

```powershell
$catalog = Get-Content .\catalog.json -Raw | ConvertFrom-Json
($catalog.entries | Where-Object identifier -eq 'urn:agentcon:agent:buyer').url = 'http://localhost:5101/.well-known/agent-card.json'
($catalog.entries | Where-Object identifier -eq 'urn:agentcon:agent:demo-wine-supplier').url = 'http://localhost:6001/.well-known/agent-card.json'
$catalogPath = Join-Path $env:TEMP 'agentcon-otel-catalog.json'
$catalog | ConvertTo-Json -Depth 10 | Set-Content $catalogPath

# Buyer terminal (with the OTLP environment above):
dotnet run --project BuyerAgent\BuyerAgent.csproj --artifacts-path .\artifacts\otel-dotnet -- `
  --Buyer:ListenUrl=http://localhost:5101 --Buyer:CatalogUrl="$catalogPath" `
  --Buyer:CopilotStateDirectory="$env:LOCALAPPDATA\AgentConDemo\otel-buyer"

# Supplier terminal (with the OTLP environment above):
& $env:ComSpec /c 'set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%" && call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul && cargo +1.98.1-x86_64-pc-windows-msvc build --manifest-path SupplierAgent\Cargo.toml --locked'
$env:SUPPLIER_PORT = '6001'
$env:SUPPLIER_COPILOT_STATE = "$env:LOCALAPPDATA\AgentConDemo\otel-supplier"
.\SupplierAgent\target\debug\agentcon-supplier.exe
```

Existing live services at 5100/6000 and their worktrees were not changed.
The isolated service processes and dashboard remain attached to this session,
not detached system services. Check readiness with `curl.exe --noproxy '*'
http://localhost:5101/health` and the equivalent supplier URL.

## Span conventions and privacy

Verified against the current, moved
[semantic-conventions-genai repository](https://github.com/open-telemetry/semantic-conventions-genai/tree/main/docs/gen-ai):
[agent spans](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-agent-spans.md),
[tool spans](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-spans.md#execute-tool-span),
and [client inference](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/client-inference.md).
These conventions are **Development/experimental**, not stable. Reviewed
2026-10-10; future convention changes may require attribute updates.

| Span | Kind | Operation / attributes |
| --- | --- | --- |
| `invoke_agent BuyerAgent` | INTERNAL | `gen_ai.operation.name=invoke_agent`, `gen_ai.agent.name=BuyerAgent` |
| Buyer outbound `invoke_agent SupplierAgent` | CLIENT | remote agent invocation, not inference |
| Rust `invoke_agent SupplierAgent` | INTERNAL | local executor agent invocation |
| `execute_tool search_supplier_catalog`, `select_supplier`, `send_supplier_message`, `get_supplier_task` | INTERNAL | `execute_tool`, `gen_ai.tool.name`, `gen_ai.tool.type=function`, buyer agent name |
| `catalog.fetch` | INTERNAL | file/HTTP transport only; HTTP fetch also has an HTTP CLIENT span |
| `supplier.business` | INTERNAL | deterministic quote/order work |
| `copilot.agent_loop` | INTERNAL | SDK work wrapper; **not an inference count** |
| `chat {observed model}` | CLIENT | `chat`, `gen_ai.request.model`, `gen_ai.response.model`, agent name; real SDK usage event |
| HTTP request spans | CLIENT / SERVER | normal HTTP conventions; health/card/task reads aren't inference |

`gen_ai.provider.name=github.copilot` is a custom provider identifier for the
Copilot hosting platform, **not** a claim that the upstream model provider is
OpenAI. Remote supplier invocation uses custom `agentcon.supplier`.
`agentcon.a2a.task.id` and `agentcon.a2a.context.id` are custom attributes, not
invented A2A semantic conventions. Use opaque demo IDs, not names/email
addresses, as these IDs are trace-visible.

The .NET SDK (1.0.19) and Rust SDK (1.0.20-preview.2) expose runtime
`TelemetryConfig` for a separate CLI-owned OTLP/file exporter and content
capture. This implementation **disables that CLI exporter explicitly**
(`COPILOT_OTEL_ENABLED=false`, capture=false) to avoid duplicate spans,
separate uncorrelated service traces, and accidental inherited content capture.
It instead consumes their real `assistant.usage` events. No SDK version was
changed. The .NET SDK **replaces** a supplied child environment, so the buyer
copies its inherited environment before setting these two overrides; otherwise
authentication and runtime settings disappear. Credentials remain process
environment values, never trace attributes or configuration files.

Each usage event yields one chat observation with the SDK-reported model,
timestamp and API-call duration when available. Token/cache/reasoning counts are
set **only when reported**. Missing usage is absent, never fabricated zero.
Missing duration produces a point observation, not an estimated duration.
An agent loop may make multiple inferences; neither wrapper spans nor the
health `inferenceAttempts` counter establish actual provider request counts.
Failed calls that emit no usage event have errors on the agent/SDK-work span,
not a fabricated chat span. SDK subscription loss produces a warning.
These reconstructed observations do not provide request-start sampling or full
runtime-internal retry visibility.

Trace attributes/events never intentionally capture prompts, completions,
system instructions, tool arguments/results, catalogue bodies, credentials,
or exception messages/stacks. Rust trace filtering excludes log events;
existing application console logs are separate and are not OTLP-exported.
Only spans from this application's Rust instrumentation target are exported,
not SDK-internal tracing spans that might include RPC payloads.
Normal HTTP instrumentation records transport metadata; do not embed secrets
or personal data in URL paths or opaque correlation IDs.

### Correlation and cancellation

The path is buyer agent -> tool -> remote invoke_agent -> .NET HTTP CLIENT ->
Rust HTTP SERVER -> Rust invoke_agent -> business or SDK agent loop -> chat.
The Rust middleware extracts W3C headers and injects its **server-span**
context back into the SDK's internal request header carrier. The pinned SDK
copies this to `ExecutorContext.service_params` before `tokio::spawn`.
The executor explicitly extracts that context and instruments its future:
work completing after an immediate HTTP response retains the same trace and
server parent. No prompts or modified A2A message metadata are needed.
.NET tools similarly capture the parent explicitly for SDK callback tasks.
Rust SDK startup runs in a detached, non-exported `agentcon_runtime` tracing
scope. Its long-lived background readers must not retain the first request's
agent span; session interpretation itself remains in the request trace. A
regression test retains a simulated runtime reader span and asserts the agent
span is still exported before runtime shutdown.

Cancellation marks interrupted execution as an error (`error.type=canceled`);
Rust's dropped executor future schedules an explicitly parented abort/disconnect
cleanup span. Cancellation does **not** imply rollback of an accepted order.
Cleanup failures are logged. .NET timeouts abort the SDK session before disposal.

## Validation without a live dashboard

Build the ephemeral Rust fixture, then test:

```powershell
& $env:ComSpec /c 'set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%" && call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul && cargo +1.98.1-x86_64-pc-windows-msvc test --manifest-path SupplierAgent\Cargo.toml --locked --quiet && cargo +1.98.1-x86_64-pc-windows-msvc build --manifest-path SupplierAgent\Cargo.toml --example trace_fixture --locked --quiet'
dotnet test BuyerAgent.Tests\BuyerAgent.Tests.csproj --artifacts-path .\artifacts\otel-dotnet
```

The cross-language test uses the **real .NET A2A client/SupplierGateway** and
real Rust SDK HTTP server/executor. An ephemeral local HTTP/protobuf collector
captures actual exports from both SDKs and asserts identical trace IDs and
exact parent span IDs through CLIENT -> SERVER -> queued invoke_agent. It
doesn't call Copilot or touch existing listeners. In-memory tests additionally
assert error statuses, tool callback parenting, real-event usage mappings,
and absence of chat/token spans for structured requests.

The full suites passed: **77 .NET tests and 5 Rust tests**. Repository catalog
tests now assert the integrated `urn:agentcon:agent:demo-wine-supplier` candidate,
its live card URL and buyer self-exclusion, without fetching the supplier card.

## Live runtime verification

Authenticated SDK inference and the standalone Aspire UI were additionally
verified on 2026-10-10, using the isolated ports and a session-local catalog.
Select **Traces** in the dashboard. These opaque trace IDs identify the smoke
checks while this dashboard process is alive:

| Trace ID | Verified result |
| --- | --- |
| `22222222222222222222222222222222` | Buyer SDK chat observation with real model, input/output/cache/reasoning usage and finish reason; no content events |
| `33333333333333333333333333333333` | 19 spans, two services: buyer catalog/tools -> HTTP -> Rust agent -> structured business quote; USD 1753.00; supplier inference attempts unchanged at zero |
| `66666666666666666666666666666666` | 21 spans, two services, depth 10: natural-language buyer -> supplier quote, including both agent spans, real chat observations in both languages and supplier business work |

The structured trace contains **buyer** chat spans: it is zero supplier
inference, not an end-to-end zero-inference transaction. Natural-language
supplier usage was verified from the real Rust SDK subscription, not fixture
data. SDK-reported counts alone populate token attributes; no prompts,
completions, tool payloads or credentials appear in these trace attributes or
events. Initial failed/superseded smoke traces may remain in the in-memory
dashboard; use the successful IDs above.

The automated cross-language test additionally exercises the SDK's
return-immediately/background-executor path and checks exact trace and parent
IDs from actual OTLP exports. A wrapper span or health counter is not proof of
provider request counts. Failed inference without usage has no fabricated chat
span. OID4VP, SLIM, real purchasing and production telemetry retention remain
outside this demo's implemented scope.
