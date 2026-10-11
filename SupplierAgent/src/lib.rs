pub mod domain;
pub mod inference;
pub mod telemetry;

use std::{
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    },
    time::Instant,
};

use a2a::*;
use a2a_server::{
    AgentExecutor, DefaultRequestHandler, ExecutorContext, InMemoryTaskStore, StaticAgentCard,
};
use axum::{
    Json, Router,
    extract::Request as HttpRequest,
    http::StatusCode,
    middleware::{self, Next},
    response::Response,
};
use futures::stream::{self, BoxStream};
use serde_json::json;
use tokio::sync::Mutex;
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;

use domain::{PURCHASE, QUOTE, RECEIPT, RFQ, Request, WineStore};
use inference::{Intent, Interpreter};

#[derive(Default)]
pub struct Metrics {
    pub structured_requests: AtomicU64,
    pub inference_attempts: AtomicU64,
}

pub struct Supplier {
    store: Mutex<WineStore>,
    interpreter: Arc<dyn Interpreter>,
    pub metrics: Arc<Metrics>,
}

impl Supplier {
    pub fn new(interpreter: Arc<dyn Interpreter>) -> Self {
        Self {
            store: Mutex::new(WineStore::default()),
            interpreter,
            metrics: Arc::new(Metrics::default()),
        }
    }

    async fn run(&self, message: &Message, context: &str) -> Result<Outcome, String> {
        if message.role != Role::User || message.parts.is_empty() {
            return Err("Expected a non-empty user message.".into());
        }
        let mut data = None;
        let mut texts = Vec::new();
        for part in &message.parts {
            match &part.content {
                PartContent::Data(value) => {
                    if data.is_some() {
                        return Err(
                            "Provide exactly one typed data part; no batch orders are supported."
                                .into(),
                        );
                    }
                    data = Some((value, part.media_type.as_deref()));
                }
                PartContent::Text(text) => texts.push(text.as_str()),
                _ => {
                    return Err(
                        "File/URL parts are not supported; send text or an agreed JSON data part."
                            .into(),
                    );
                }
            }
        }
        if let Some((value, media_type)) = data {
            self.metrics
                .structured_requests
                .fetch_add(1, Ordering::Relaxed);
            let request: Request = serde_json::from_value(value.clone())
                .map_err(|e| format!("Invalid agreed v1 request (no inference attempted): {e}"))?;
            if let Some(media_type) = media_type {
                if media_type != request.media_type() && media_type != "application/json" {
                    return Err("Data part mediaType does not match its kind/version.".into());
                }
            }
            return self.apply(request, context).await;
        }
        if message
            .parts
            .iter()
            .any(|p| p.media_type.as_deref().is_some_and(|m| m != "text/plain"))
        {
            return Err("Natural-language text parts must use text/plain.".into());
        }
        let text = texts.join("\n");
        if text.trim().is_empty() || text.len() > 16_384 {
            return Err("Natural-language input must contain 1-16384 bytes.".into());
        }
        let latest = self
            .store
            .lock()
            .await
            .latest_quote(context)
            .map(str::to_owned);
        self.metrics
            .inference_attempts
            .fetch_add(1, Ordering::Relaxed);
        tracing::info!("supplier Copilot inference attempt");
        let intent = self
            .interpreter
            .interpret(&text, latest.as_deref(), context)
            .await?;
        match intent {
            Intent::Quote { request: request @ Request::Rfq { .. } } |
            Intent::Purchase { request: request @ Request::PurchaseOrder { .. } } => self.apply(request, context).await,
            Intent::Quote { .. } | Intent::Purchase { .. } => Err("Copilot intent/request mismatch; no order executed.".into()),
            Intent::InputRequired { question } => Ok(Outcome { state: TaskState::InputRequired, text: question, data: None }),
            Intent::AuthorizationRequired => Ok(Outcome {
                state: TaskState::AuthRequired,
                text: "Real purchasing and OID4VP verification are NOT IMPLEMENTED. No presentation request, \
                       QR code or verifier exists here. A message asserting age is not proof. \
                       Only demoOnly:true simulated orders can complete.".into(),
                data: None,
            }),
            Intent::Help => Ok(Outcome {
                state: TaskState::Completed,
                text: "AgentCon TEST-ONLY supplier: 2019 Barolo demo quotes and simulated orders. \
                       Natural language uses the official Copilot Rust SDK; agreed typed data parts \
                       use deterministic code. No real purchasing, payment, fulfillment, age proof, \
                       OID4VP verifier or SLIM integration.".into(), data: None,
            }),
        }
    }

    async fn apply(&self, request: Request, context: &str) -> Result<Outcome, String> {
        let span = tracing::info_span!("supplier.business", agentcon.a2a.context.id = context);
        let result = async { self.store.lock().await.apply(request, context) }
            .instrument(span.clone())
            .await;
        if result.is_err() {
            telemetry::error(&span, "invalid_business_request");
        }
        let (media_type, data, text) = result?;
        Ok(Outcome {
            state: TaskState::Completed,
            text,
            data: Some((media_type, data)),
        })
    }
}

struct Outcome {
    state: TaskState,
    text: String,
    data: Option<(&'static str, serde_json::Value)>,
}

struct SupplierExecutor(Arc<Supplier>);

struct ExecutionTrace {
    span: tracing::Span,
    completed: bool,
}

impl Drop for ExecutionTrace {
    fn drop(&mut self) {
        if !self.completed {
            telemetry::error(&self.span, "canceled");
        }
    }
}

impl AgentExecutor for SupplierExecutor {
    fn execute(
        &self,
        ctx: ExecutorContext,
    ) -> BoxStream<'static, Result<StreamResponse, A2AError>> {
        let supplier = self.0.clone();
        let span = tracing::info_span!("invoke_agent SupplierAgent", otel.kind = "internal",
            gen_ai.operation.name = "invoke_agent", gen_ai.agent.name = "SupplierAgent",
            gen_ai.provider.name = "github.copilot", agentcon.a2a.task.id = %ctx.task_id,
            agentcon.a2a.context.id = %ctx.context_id);
        let _ = span.set_parent(telemetry::executor_parent(&ctx.service_params));
        let execution_span = span.clone();
        Box::pin(stream::once(async move {
            let mut execution = ExecutionTrace { span: execution_span.clone(), completed: false };
            let started = Instant::now();
            let outcome = match &ctx.message {
                Some(message) => supplier.run(message, &ctx.context_id).await,
                None => Err("Missing user message.".into()),
            };
            let outcome = outcome.unwrap_or_else(|error| {
                telemetry::error(&execution_span, "supplier_request_failed");
                tracing::warn!(task_id = %ctx.task_id, "supplier request failed");
                Outcome {
                    state: TaskState::Failed,
                    text: error,
                    data: None,
                }
            });
            execution.completed = true;
            tracing::info!(task_id = %ctx.task_id, state = ?outcome.state, elapsed_ms = started.elapsed().as_millis(), "supplier execution");
            let mut response = Message::new(Role::Agent, vec![Part::text(outcome.text)]);
            response.task_id = Some(ctx.task_id.clone());
            response.context_id = Some(ctx.context_id.clone());
            let artifacts = outcome.data.map(|(media_type, data)| {
                vec![Artifact {
                    artifact_id: uuid::Uuid::new_v4().to_string(),
                    name: Some("AgentCon demo result".into()),
                    description: Some("TEST ONLY: no real purchase or age verification.".into()),
                    parts: vec![Part::data(data).with_media_type(media_type)],
                    metadata: None,
                    extensions: None,
                }]
            });
            Ok(StreamResponse::Task(Task {
                id: ctx.task_id,
                context_id: ctx.context_id,
                status: TaskStatus {
                    state: outcome.state,
                    message: Some(response),
                    timestamp: Some(chrono::Utc::now()),
                },
                artifacts,
                history: None,
                metadata: None,
            }))
        }.instrument(span)))
    }

    fn cancel(&self, ctx: ExecutorContext) -> BoxStream<'static, Result<StreamResponse, A2AError>> {
        let span = tracing::info_span!("supplier.cancel", agentcon.a2a.task.id = %ctx.task_id);
        let _ = span.set_parent(telemetry::executor_parent(&ctx.service_params));
        Box::pin(stream::once(async move {
            Ok(StreamResponse::StatusUpdate(TaskStatusUpdateEvent {
                task_id: ctx.task_id,
                context_id: ctx.context_id,
                status: TaskStatus {
                    state: TaskState::Canceled,
                    timestamp: Some(chrono::Utc::now()),
                    message: Some(Message::new(
                        Role::Agent,
                        vec![Part::text(
                            "Task canceled. A recorded simulated order is not rolled back; inspect its receipt before retrying.",
                        )],
                    )),
                },
                metadata: None,
            }))
        }.instrument(span)))
    }
}

pub fn card(origin: &str) -> AgentCard {
    AgentCard {
        name: "AgentCon Demo Wine Supplier".into(),
        description: "TEST ONLY. Rust supplier: Copilot-mediated NL quotes and simulated purchases; deterministic v1 RFQ/order profiles. No real purchases or age verification.".into(),
        version: env!("CARGO_PKG_VERSION").into(),
        provider: Some(AgentProvider { organization: "AgentCon demo".into(), url: "https://github.com/darrelmiller/AgentCon".into() }),
        capabilities: AgentCapabilities { streaming: Some(true), push_notifications: Some(false), extensions: None, extended_agent_card: None },
        skills: vec![
            AgentSkill {
                id: "demo-wine".into(), name: "Demo wine quote and order".into(),
                description: "2019 Barolo, 1-48 cases, San Jose. Natural language invokes Copilot. Typed schemaVersion:1, kind:rfq/purchase-order data is deterministic. demoOnly:true required; artifacts carry typed quote/receipt. OID4VP and SLIM are future work.".into(),
                tags: vec!["wine".into(), "supplier".into(), "demo".into()],
                examples: Some(vec!["Quote 12 cases of 2019 Barolo delivered to San Jose for the demo.".into()]),
                input_modes: Some(vec!["text/plain".into(), "application/json".into(), RFQ.into(), PURCHASE.into()]),
                output_modes: Some(vec!["text/plain".into(), QUOTE.into(), RECEIPT.into()]),
                security_requirements: None,
            },
        ],
        default_input_modes: vec!["text/plain".into(), "application/json".into(), RFQ.into(), PURCHASE.into()],
        default_output_modes: vec!["text/plain".into(), QUOTE.into(), RECEIPT.into()],
        supported_interfaces: vec![AgentInterface::new(format!("{origin}/a2a"), TRANSPORT_PROTOCOL_JSONRPC)],
        security_schemes: None, security_requirements: None,
        documentation_url: Some("https://github.com/darrelmiller/AgentCon/tree/main/SupplierAgent".into()),
        icon_url: None, signatures: None,
    }
}

async fn reject_browser(request: HttpRequest, next: Next) -> Response {
    if request.headers().contains_key("origin") || request.headers().contains_key("sec-fetch-site")
    {
        return (
            StatusCode::FORBIDDEN,
            "Browser-origin requests are not permitted.",
        )
            .into_response();
    }
    next.run(request).await
}

use axum::response::IntoResponse;

pub fn app(supplier: Arc<Supplier>, origin: &str) -> Router {
    let metrics = supplier.metrics.clone();
    let handler = Arc::new(DefaultRequestHandler::new(
        SupplierExecutor(supplier),
        InMemoryTaskStore::new(),
    ));
    Router::new()
        .nest("/a2a", a2a_server::jsonrpc::jsonrpc_router(handler))
        .merge(a2a_server::agent_card::agent_card_router(Arc::new(
            StaticAgentCard::new(card(origin)),
        )))
        .route(
            "/health",
            axum::routing::get(move || async move {
                Json(json!({
                    "status": "ok", "demoOnly": true,
                    "structuredRequests": metrics.structured_requests.load(Ordering::Relaxed),
                    "inferenceAttempts": metrics.inference_attempts.load(Ordering::Relaxed),
                    "oid4vp": "not-implemented", "slim": "not-implemented"
                }))
            }),
        )
        .layer(middleware::from_fn(reject_browser))
        .layer(middleware::from_fn(telemetry::http))
}
