pub mod authorization;
pub mod domain;
pub mod inference;
pub mod slim;
pub mod telemetry;

use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::Instant,
};

use a2a::*;
use a2a_server::{AgentExecutor, DefaultRequestHandler, ExecutorContext, InMemoryTaskStore};
use axum::{
    Json, Router,
    extract::Request as HttpRequest,
    http::StatusCode,
    middleware::{self, Next},
    response::Response,
};
use futures::stream::{self, BoxStream};
use futures::StreamExt;
use serde_json::json;
use tokio::sync::Mutex;
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;

use domain::{Inventory, PURCHASE, QUOTE, RECEIPT, RFQ, Request, WineStore};
use inference::{Intent, Interpreter};

#[derive(Default)]
pub struct Metrics {
    pub structured_requests: AtomicU64,
    pub inference_attempts: AtomicU64,
}

pub struct Supplier {
    inventory: &'static Inventory,
    store: Mutex<WineStore>,
    interpreter: Arc<dyn Interpreter>,
    pub metrics: Arc<Metrics>,
    verifier: Option<Arc<dyn authorization::Verifier>>,
    pending: Mutex<std::collections::HashMap<String, PendingPurchase>>,
    structured: AtomicBool,
    control_token: Option<String>,
    /// SLIMRPC interface URL (slim://org/namespace/app), if configured.
    slim_url: Option<String>,
    /// Set once the app is registered on the SLIM node; only then is SLIMRPC advertised.
    slim_ready: AtomicBool,
}

struct PendingPurchase {
    request: Request,
    challenge: authorization::Challenge,
}

impl Supplier {
    pub fn new(interpreter: Arc<dyn Interpreter>) -> Self {
        Self {
            inventory: &domain::BAROLO,
            store: Mutex::new(WineStore::default()),
            interpreter,
            metrics: Arc::new(Metrics::default()),
            verifier: None,
            pending: Mutex::new(std::collections::HashMap::new()),
            structured: AtomicBool::new(true),
            control_token: None,
            slim_url: None,
            slim_ready: AtomicBool::new(false),
        }
    }

    pub fn with_verifier(mut self, verifier: Arc<dyn authorization::Verifier>) -> Self {
        self.verifier = Some(verifier);
        self
    }

    /// The SLIMRPC interface (see `slim::serve`) to advertise once `mark_slim_ready` is called.
    pub fn with_slim_url(mut self, url: String) -> Self {
        self.slim_url = Some(url);
        self
    }

    /// Called after registration on the SLIM node: the Agent Card and /health now advertise SLIMRPC.
    pub fn mark_slim_ready(&self) {
        self.slim_ready.store(true, Ordering::Relaxed);
    }

    fn advertised_slim_url(&self) -> Option<&str> {
        self.slim_ready.load(Ordering::Relaxed).then_some(self.slim_url.as_deref()).flatten()
    }

    pub fn with_inventory(mut self, inventory: &'static Inventory) -> Self {
        self.inventory = inventory;
        self.store = Mutex::new(WineStore::new(inventory));
        self
    }

    pub fn with_structured(self, enabled: bool) -> Self {
        self.structured.store(enabled, Ordering::Relaxed);
        self
    }

    pub fn with_control_token(mut self, token: String) -> Result<Self, String> {
        if token.len() < 32
            || token.len() > 256
            || !token
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
        {
            return Err(
                "Supplier control token must contain 32-256 ASCII letters, digits, '-' or '_'."
                    .into(),
            );
        }
        self.control_token = Some(token);
        Ok(self)
    }

    async fn run(
        &self,
        message: &Message,
        context: &str,
        task: &str,
        accepted: Option<&[String]>,
        structured: bool,
    ) -> Result<Outcome, String> {
        if message.role != Role::User || message.parts.is_empty() {
            return Err("Expected a non-empty user message.".into());
        }
        let typed = message
            .parts
            .iter()
            .any(|part| matches!(part.content, PartContent::Data(_)));
        if typed {
            if !structured {
                return Err(
                    "Structured input is disabled; send text/plain. No inference attempted.".into(),
                );
            }
            if message.parts.len() != 1 {
                return Err("Provide exactly one typed data part without text.".into());
            }
            let part = &message.parts[0];
            if !matches!(part.media_type.as_deref(), Some(RFQ | PURCHASE)) {
                return Err("Only advertised wine-v1 vendor input media types are accepted; no generic application/json.".into());
            }
        } else if message.parts.iter().any(|part| {
            !matches!(part.content, PartContent::Text(_))
                || part
                    .media_type
                    .as_deref()
                    .is_some_and(|value| value != "text/plain")
        }) {
            return Err("Only text/plain text parts are accepted.".into());
        }
        let mut pending = self.pending.lock().await;
        pending
            .retain(|_, purchase| purchase.challenge.expires_at > chrono::Utc::now().timestamp());
        if let Some(purchase) = pending.get(task) {
            if purchase.challenge.context_id != context {
                return Err("Purchase authorization belongs to another context.".into());
            }
            let verifier = self
                .verifier
                .as_ref()
                .ok_or("Presentation verifier is not configured.")?;
            if !verifier.verified(&purchase.challenge).await? {
                return Ok(Outcome::authorization(&purchase.challenge));
            }
            // Resume the immutable purchase that was authorized, not new model-supplied data.
            let purchase = pending
                .remove(task)
                .expect("Pending purchase exists while locked");
            drop(pending);
            let mut outcome = self
                .apply_authorized(purchase.request, context, accepted)
                .await?;
            if let Some((_, data)) = &mut outcome.data {
                data["ageVerification"] = json!("verified-demo-over-21");
            }
            return Ok(outcome);
        }
        drop(pending);
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
            if media_type != Some(request.media_type()) {
                return Err("Data part must declare the agreed vendor mediaType matching its kind/version; generic application/json is not accepted.".into());
            }
            return self.apply(request, context, task, accepted).await;
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
            Intent::Purchase { request: request @ Request::PurchaseOrder { .. } } => self.apply(request, context, task, accepted).await,
            Intent::Quote { .. } | Intent::Purchase { .. } => Err("Copilot intent/request mismatch; no order executed.".into()),
            Intent::InputRequired { question } => Ok(Outcome { state: TaskState::InputRequired, text: question, data: None, authorization: None }),
            Intent::AuthorizationRequired => Ok(Outcome {
                state: TaskState::AuthRequired,
                text: "Send a quote-bound purchase request to initiate wallet authorization. No order has been created.".into(),
                data: None,
                authorization: None,
            }),
            Intent::Help => Ok(Outcome {
                state: TaskState::Completed,
                text: format!("{}: {} quotes and orders, with delivery in the US or Canada. \
                       Natural language uses the official Copilot Rust SDK; agreed typed data parts \
                       use deterministic code.", self.inventory.supplier_name, self.inventory.summary()), data: None, authorization: None,
            }),
        }
    }

    async fn apply(
        &self,
        request: Request,
        context: &str,
        task: &str,
        accepted: Option<&[String]>,
    ) -> Result<Outcome, String> {
        if matches!(&request, Request::PurchaseOrder { .. }) {
            self.store
                .lock()
                .await
                .validate_purchase(&request, context)?;
            if accepted.is_some_and(|modes| !modes.iter().any(|mode| mode == "text/plain")) {
                return Err(
                    "Purchase authorization requires text/plain status messages; no order created."
                        .into(),
                );
            }
            let Some(verifier) = &self.verifier else {
                return Ok(Outcome {
                    state: TaskState::AuthRequired,
                    text: "Wallet authorization is required before purchasing. The supplier presentation verifier is not configured; no order has been created.".into(),
                    data: None, authorization: None,
                });
            };
            let mut pending = self.pending.lock().await;
            pending.retain(|_, purchase| {
                purchase.challenge.expires_at > chrono::Utc::now().timestamp()
            });
            if pending.len() >= 128 {
                return Err("Pending purchase authorization capacity reached.".into());
            }
            let challenge = verifier.create(task, context).await?;
            let outcome = Outcome::authorization(&challenge);
            pending.insert(task.into(), PendingPurchase { request, challenge });
            return Ok(outcome);
        }
        self.apply_authorized(request, context, accepted).await
    }

    async fn resume_verified(
        &self, task: &str, context: &str, accepted: Option<&[String]>,
    ) -> Result<Option<Outcome>, String> {
        let mut pending = self.pending.lock().await;
        let Some(purchase) = pending.get(task) else {
            return Err("Pending purchase no longer exists; inspect the original task.".into());
        };
        if purchase.challenge.context_id != context {
            return Err("Purchase authorization belongs to another context.".into());
        }
        if purchase.challenge.expires_at <= chrono::Utc::now().timestamp() {
            pending.remove(task);
            return Err("Wallet authorization expired; no order has been created.".into());
        }
        let verifier = self.verifier.as_ref().ok_or("Presentation verifier is not configured.")?;
        if !verifier.verified(&purchase.challenge).await? {
            return Ok(None);
        }
        let purchase = pending.remove(task).expect("Pending purchase exists while locked");
        drop(pending);
        let mut outcome = self.apply_authorized(purchase.request, context, accepted).await?;
        if let Some((_, data)) = &mut outcome.data {
            data["ageVerification"] = json!("verified-demo-over-21");
        }
        Ok(Some(outcome))
    }

    async fn apply_authorized(
        &self,
        request: Request,
        context: &str,
        accepted: Option<&[String]>,
    ) -> Result<Outcome, String> {
        let output = match &request {
            Request::Rfq { .. } => QUOTE,
            Request::PurchaseOrder { .. } => RECEIPT,
        };
        if accepted.is_some_and(|modes| {
            !modes
                .iter()
                .any(|mode| mode == "text/plain" || mode == output)
        }) {
            return Err(
                "No accepted output type for this request; no business operation executed.".into(),
            );
        }
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
            authorization: None,
        })
    }
}

struct Outcome {
    state: TaskState,
    text: String,
    data: Option<(&'static str, serde_json::Value)>,
    authorization: Option<authorization::Challenge>,
}

impl Outcome {
    fn authorization(challenge: &authorization::Challenge) -> Self {
        Self {
            state: TaskState::AuthRequired,
            text: format!(
                "Wallet authorization is required before this purchase. Open {} and scan the QR code with your OID4VP wallet. Select the AgentCon over_21 credential and approve disclosure. Then return to the buyer and ask it to continue this pending purchase. No order has been created.",
                challenge.presentation_url
            ),
            data: None,
            authorization: Some(challenge.clone()),
        }
    }
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
        let watcher_span = span.clone();
        let watch_supplier = supplier.clone();
        let watch_task = ctx.task_id.clone();
        let watch_context = ctx.context_id.clone();
        let watch_structured = supplier.structured.load(Ordering::Relaxed);
        let watch_accepted: Option<Vec<String>> = ctx.metadata.as_ref()
            .and_then(|metadata| metadata.get("agentcon.internal.acceptedOutputModes"))
            .and_then(|value| serde_json::from_value(value.clone()).ok());
        let watch_enabled = ctx.metadata.as_ref().and_then(|metadata|
            metadata.get("agentcon.internal.watchAuthorization")).and_then(|value| value.as_bool()) == Some(true);
        // Report the task as working first (standard A2A lifecycle): a returnImmediately caller gets this and polls
        // GetTask while the final event below completes into the task store.
        let working = Task {
            id: ctx.task_id.clone(),
            context_id: ctx.context_id.clone(),
            status: TaskStatus { state: TaskState::Working, message: None, timestamp: Some(chrono::Utc::now()) },
            artifacts: None,
            history: None,
            metadata: None,
        };
        let initial = stream::once(async move {
            let mut execution = ExecutionTrace { span: execution_span.clone(), completed: false };
            let started = Instant::now();
            let structured = watch_structured;
            let accepted: Option<Vec<String>> = ctx.metadata.as_ref()
                .and_then(|metadata| metadata.get("agentcon.internal.acceptedOutputModes"))
                .map(|value| serde_json::from_value(value.clone()))
                .transpose().map_err(|_| A2AError::invalid_params("Invalid acceptedOutputModes"))?;
            let text_accepted = accepted.as_ref().is_none_or(|modes| modes.iter().any(|mode| mode == "text/plain"));
            if accepted.as_ref().is_some_and(|modes| !modes.iter().any(|mode|
                mode == "text/plain" || structured && matches!(mode.as_str(), QUOTE | RECEIPT))) {
                telemetry::error(&execution_span, "incompatible_output_modes");
                execution.completed = true;
                return Err(A2AError::content_type_not_supported());
            }
            let (outcome, llm_calls, count_complete) = inference::MESSAGE_LLM_CALLS.scope(AtomicU64::new(0),
                inference::MESSAGE_LLM_COMPLETE.scope(std::sync::atomic::AtomicBool::new(true), async {
                let outcome = match &ctx.message {
                    Some(message) => supplier.run(message, &ctx.context_id, &ctx.task_id, accepted.as_deref(), structured).await,
                    None => Err("Missing user message.".into()),
                };
                (outcome, inference::MESSAGE_LLM_CALLS.with(|calls| calls.load(Ordering::Relaxed)),
                    inference::MESSAGE_LLM_COMPLETE.with(|complete| complete.load(Ordering::Relaxed)))
            })).await;
            if !text_accepted && outcome.as_ref().map_or(true, |outcome| outcome.data.is_none()) {
                telemetry::error(&execution_span, "incompatible_output_modes");
                execution.completed = true;
                return Err(A2AError::content_type_not_supported());
            }
            let mut outcome = outcome.unwrap_or_else(|error| {
                telemetry::error(&execution_span, "supplier_request_failed");
                tracing::warn!(task_id = %ctx.task_id, "supplier request failed");
                Outcome {
                    state: TaskState::Failed,
                    text: error,
                    data: None,
                    authorization: None,
                }
            });
            if watch_enabled && let Some(challenge) = &outcome.authorization {
                outcome.text = format!(
                    "Wallet authorization is required before this purchase. Open {} and scan the QR code with your OID4VP wallet. Select the AgentCon over_21 credential and approve disclosure. This original purchase will complete automatically after task-bound verifier approval. Do not submit another order. No order has been created yet.",
                    challenge.presentation_url);
            }
            execution.completed = true;
            tracing::info!(task_id = %ctx.task_id, state = ?outcome.state, elapsed_ms = started.elapsed().as_millis(), "supplier execution");
            let result_text = outcome.text.clone();
            let mut response = Message::new(Role::Agent, vec![Part::text(outcome.text)]);
            response.metadata = Some(std::collections::HashMap::from([
                ("agentcon.llmCalls".into(), json!(llm_calls)),
                ("agentcon.llmCallsComplete".into(), json!(count_complete))
            ]));
            if let Some(challenge) = &outcome.authorization {
                response.metadata.as_mut().unwrap().insert(authorization::EXTENSION.into(),
                    json!({"authorizationRequest": challenge.authorization_request}));
                response.metadata.as_mut().unwrap().insert("agentcon.presentation".into(),
                    json!({"presentationUrl": challenge.presentation_url, "walletUri": challenge.wallet_uri,
                        "expiresAt": challenge.expires_at, "supplierTaskId": ctx.task_id,
                        "supplierContextId": ctx.context_id, "demoOnly": true}));
            }
            response.task_id = Some(ctx.task_id.clone());
            response.context_id = Some(ctx.context_id.clone());
            let artifacts = outcome.data.map(|(media_type, data)| {
                let typed_accepted = structured &&
                    accepted.as_ref().is_none_or(|modes| modes.iter().any(|mode| mode == media_type));
                vec![Artifact {
                    artifact_id: uuid::Uuid::new_v4().to_string(),
                    name: Some(if media_type == QUOTE { "quote" } else { "order-receipt" }.into()),
                    description: Some(if media_type == QUOTE { "Wine quote" } else { "Order receipt" }.into()),
                    parts: if typed_accepted { vec![Part::data(data).with_media_type(media_type)] }
                        else { vec![Part::text(result_text).with_media_type("text/plain")] },
                    metadata: Some(std::collections::HashMap::from([
                        ("agentcon.llmCalls".into(), json!(llm_calls)),
                        ("agentcon.llmCallsComplete".into(), json!(count_complete))
                    ])),
                    extensions: None,
                }]
            });
            Ok(StreamResponse::Task(Task {
                id: ctx.task_id,
                context_id: ctx.context_id,
                status: TaskStatus {
                    state: outcome.state,
                    message: text_accepted.then_some(response),
                    timestamp: Some(chrono::Utc::now()),
                },
                artifacts,
                history: None,
                metadata: Some(std::collections::HashMap::from([
                    ("agentcon.llmCalls".into(), json!(llm_calls)),
                    ("agentcon.llmCallsComplete".into(), json!(count_complete))
                ])),
            }))
        }.instrument(span));
        let initial = stream::once(std::future::ready(Ok(StreamResponse::Task(working)))).chain(initial);
        Box::pin(initial.flat_map(move |event| {
            let watching = watch_enabled && matches!(&event,
                Ok(StreamResponse::Task(task)) if task.status.state == TaskState::AuthRequired &&
                    task.status.message.as_ref().and_then(|message| message.metadata.as_ref())
                        .is_some_and(|metadata| metadata.contains_key(authorization::EXTENSION)));
            let supplier = watch_supplier.clone();
            let task_id = watch_task.clone();
            let context_id = watch_context.clone();
            let accepted = watch_accepted.clone();
            let updates = stream::once(async move {
                if !watching { return Vec::new(); }
                let outcome = loop {
                    tokio::time::sleep(std::time::Duration::from_secs(1)).await;
                    match supplier.resume_verified(&task_id, &context_id, accepted.as_deref()).await {
                        Ok(None) => continue,
                        Ok(Some(outcome)) => break outcome,
                        Err(error) => {
                            tracing::warn!(%task_id, "automatic authorization completion failed");
                            break Outcome { state: TaskState::Failed, text: error, data: None, authorization: None };
                        }
                    }
                };
                let metadata = std::collections::HashMap::from([
                    ("agentcon.llmCalls".into(), json!(0)),
                    ("agentcon.llmCallsComplete".into(), json!(true))
                ]);
                let mut events = Vec::new();
                if let Some((media_type, data)) = outcome.data {
                    let typed = watch_structured &&
                        accepted.as_ref().is_none_or(|modes| modes.iter().any(|mode| mode == media_type));
                    events.push(Ok(StreamResponse::ArtifactUpdate(TaskArtifactUpdateEvent {
                        task_id: task_id.clone(), context_id: context_id.clone(),
                        artifact: Artifact {
                            artifact_id: uuid::Uuid::new_v4().to_string(), name: Some("order-receipt".into()),
                            description: Some("Verified demo order receipt".into()),
                            parts: if typed { vec![Part::data(data).with_media_type(media_type)] }
                                else { vec![Part::text(outcome.text.clone()).with_media_type("text/plain")] },
                            metadata: Some(metadata.clone()), extensions: None,
                        },
                        append: Some(false), last_chunk: Some(true), metadata: None,
                    })));
                }
                let mut message = Message::new(Role::Agent, vec![Part::text(outcome.text)]);
                message.task_id = Some(task_id.clone());
                message.context_id = Some(context_id.clone());
                message.metadata = Some(metadata);
                events.push(Ok(StreamResponse::StatusUpdate(TaskStatusUpdateEvent {
                    task_id, context_id, status: TaskStatus {
                        state: outcome.state, message: Some(message), timestamp: Some(chrono::Utc::now()),
                    }, metadata: None,
                })));
                events
            }.instrument(watcher_span.clone())).flat_map(stream::iter);
            stream::once(async move { event }).chain(updates)
        }))
    }

    fn cancel(&self, ctx: ExecutorContext) -> BoxStream<'static, Result<StreamResponse, A2AError>> {
        let supplier = self.0.clone();
        let span = tracing::info_span!("supplier.cancel", agentcon.a2a.task.id = %ctx.task_id);
        let _ = span.set_parent(telemetry::executor_parent(&ctx.service_params));
        Box::pin(stream::once(async move {
            supplier.pending.lock().await.remove(&ctx.task_id);
            Ok(StreamResponse::StatusUpdate(TaskStatusUpdateEvent {
                task_id: ctx.task_id,
                context_id: ctx.context_id,
                status: TaskStatus {
                    state: TaskState::Canceled,
                    timestamp: Some(chrono::Utc::now()),
                    message: Some({
                        let mut message = Message::new(
                        Role::Agent,
                        vec![Part::text(
                            "Task canceled. A recorded simulated order is not rolled back; inspect its receipt before retrying.",
                        )],
                        );
                        message.metadata = Some(std::collections::HashMap::from([
                            ("agentcon.llmCalls".into(), json!(0))
                        ]));
                        message
                    }),
                },
                metadata: None,
            }))
        }.instrument(span)))
    }
}

pub fn card(origin: &str, inventory: &Inventory, slim_url: Option<&str>) -> AgentCard {
    AgentCard {
        name: inventory.supplier_name.clone(),
        description: format!("Wine supplier of {}: Copilot-mediated natural-language quotes and orders; deterministic v1 RFQ/order profiles. Delivery throughout the US and Canada.", inventory.summary()),
        version: env!("CARGO_PKG_VERSION").into(),
        provider: Some(AgentProvider { organization: "AgentCon demo".into(), url: "https://github.com/darrelmiller/AgentCon".into() }),
        capabilities: AgentCapabilities { streaming: Some(true), push_notifications: Some(false),
            extensions: Some(serde_json::from_value(json!([{
                "uri": authorization::EXTENSION,
                "description": "Purchase tasks require a task-bound presentation of the issuer's demo over_21 credential.",
                "required": false, "params": {"oid4vpVersions": ["1.0"]}
            }, {
                "uri": "urn:agentcon:extension:structured-schemas:v1",
                "description": "Demo-only schema discovery: media type to JSON Schema URL and local definition.",
                "required": false,
                "params": {"schemas": [
                    {"mediaType": RFQ, "url": format!("{origin}/schemas/wine-v1.schema.json"), "definition": "rfq", "direction": "input"},
                    {"mediaType": PURCHASE, "url": format!("{origin}/schemas/wine-v1.schema.json"), "definition": "purchaseOrder", "direction": "input"},
                    {"mediaType": QUOTE, "url": format!("{origin}/schemas/wine-v1.schema.json"), "definition": "quote", "direction": "output"},
                    {"mediaType": RECEIPT, "url": format!("{origin}/schemas/wine-v1.schema.json"), "definition": "receipt", "direction": "output"}
                ]}
            }])).expect("Static schema discovery extension must match A2A SDK types")),
            extended_agent_card: None },
        skills: vec![
            AgentSkill {
                id: "demo-wine".into(), name: "Demo wine quote and order".into(),
                description: format!("{}, 1-48 cases, delivery anywhere in the US or Canada. {} plus USD {} flat delivery in either country. Natural language invokes Copilot. Typed schemaVersion:1, kind:rfq/purchase-order data is deterministic; deliveryCountry US/CA (default US). demoOnly:true required; artifacts carry typed quote/receipt.", inventory.summary(), inventory.price_list(), inventory.delivery_minor / 100),
                tags: vec!["wine".into(), "supplier".into(), "demo".into()],
                examples: Some(inventory.wines.iter().map(|w| format!("Quote 12 cases of {} {} delivered to San Jose for the demo.", w.vintage, w.name)).collect()),
                input_modes: Some(vec!["text/plain".into(), RFQ.into(), PURCHASE.into()]),
                output_modes: Some(vec!["text/plain".into(), QUOTE.into(), RECEIPT.into()]),
                security_requirements: None,
            },
        ],
        default_input_modes: vec!["text/plain".into(), RFQ.into(), PURCHASE.into()],
        default_output_modes: vec!["text/plain".into(), QUOTE.into(), RECEIPT.into()],
        supported_interfaces: std::iter::once(AgentInterface::new(format!("{origin}/a2a"), TRANSPORT_PROTOCOL_JSONRPC))
            .chain(slim_url.map(|url| AgentInterface::new(url.to_owned(), a2a::TRANSPORT_PROTOCOL_SLIMRPC)))
            .collect(),
        security_schemes: None, security_requirements: None,
        documentation_url: Some("https://github.com/darrelmiller/AgentCon/tree/main/SupplierAgent".into()),
        icon_url: None, signatures: None,
    }
}

pub fn configured_card(origin: &str, structured: bool, inventory: &Inventory, slim_url: Option<&str>) -> AgentCard {
    let mut value = card(origin, inventory, slim_url);
    if !structured {
        value.description = "Wine supplier: text/plain quotes and orders via Copilot. Structured data parts are disabled.".into();
        value.default_input_modes = vec!["text/plain".into()];
        value.default_output_modes = vec!["text/plain".into()];
        for skill in &mut value.skills {
            skill.input_modes = Some(vec!["text/plain".into()]);
            skill.output_modes = Some(vec!["text/plain".into()]);
            skill.description =
                format!("{} demo quotes/orders via Copilot text/plain. Typed data is disabled.", inventory.summary());
        }
        if let Some(extensions) = &mut value.capabilities.extensions {
            extensions.retain(|extension| {
                extension.uri != "urn:agentcon:extension:structured-schemas:v1"
            });
        }
    }
    value
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

// The pinned SDK omits configuration from ExecutorContext. Bridge only output
// negotiation into request metadata before dispatch; never retain message bodies.
async fn preserve_output_modes(request: HttpRequest, next: Next) -> Response {
    let (parts, body) = request.into_parts();
    let bytes = match axum::body::to_bytes(body, 128 * 1024).await {
        Ok(bytes) => bytes,
        Err(_) => {
            return (
                StatusCode::PAYLOAD_TOO_LARGE,
                "A2A request exceeds 128 KiB or could not be read.",
            )
                .into_response();
        }
    };
    let mut value: serde_json::Value = match serde_json::from_slice(&bytes) {
        Ok(value) => value,
        Err(_) => {
            return next
                .run(HttpRequest::from_parts(
                    parts,
                    axum::body::Body::from(bytes),
                ))
                .await;
        }
    };
    let streaming = matches!(value.get("method").and_then(|method| method.as_str()),
        Some("SendStreamingMessage" | "message/stream"));
    if let Some(params) = value
        .get_mut("params")
        .and_then(|params| params.as_object_mut())
    {
        let accepted = params
            .get("configuration")
            .and_then(|config| config.get("acceptedOutputModes"))
            .cloned();
        if let Some(modes) = accepted.as_ref() {
            if !modes.as_array().is_some_and(|modes| {
                modes.len() <= 16
                    && modes.iter().all(|mode| {
                        mode.as_str()
                            .is_some_and(|mode| !mode.is_empty() && mode.len() <= 256)
                    })
            }) {
                return (StatusCode::BAD_REQUEST, "Invalid acceptedOutputModes.").into_response();
            }
        }
        let metadata = params.entry("metadata").or_insert_with(|| json!({}));
        if metadata.is_null() {
            *metadata = json!({});
        }
        if let Some(metadata) = metadata.as_object_mut() {
            metadata.insert("agentcon.internal.watchAuthorization".into(), json!(streaming));
            metadata.remove("agentcon.internal.acceptedOutputModes");
            if let Some(accepted) = accepted {
                metadata.insert("agentcon.internal.acceptedOutputModes".into(), accepted);
            }
        }
    }
    let bytes = match serde_json::to_vec(&value) {
        Ok(bytes) => bytes,
        Err(_) => {
            return (
                StatusCode::INTERNAL_SERVER_ERROR,
                "Could not preserve output negotiation.",
            )
                .into_response();
        }
    };
    let mut request = HttpRequest::from_parts(parts, axum::body::Body::from(bytes));
    request
        .headers_mut()
        .remove(axum::http::header::CONTENT_LENGTH);
    next.run(request).await
}

/// The supplier's A2A request handler. Share one instance across bindings (HTTP JSON-RPC and
/// SLIMRPC) so both see the same tasks.
pub fn request_handler(supplier: Arc<Supplier>) -> Arc<DefaultRequestHandler> {
    Arc::new(DefaultRequestHandler::new(SupplierExecutor(supplier), InMemoryTaskStore::new()))
}

pub fn app(supplier: Arc<Supplier>, origin: &str) -> Router {
    let handler = request_handler(supplier.clone());
    app_with_handler(supplier, origin, handler)
}

pub fn app_with_handler(
    supplier: Arc<Supplier>,
    origin: &str,
    handler: Arc<DefaultRequestHandler>,
) -> Router {
    let metrics = supplier.metrics.clone();
    let verifier_configured = supplier.verifier.is_some();
    let schema_supplier = supplier.clone();
    let card_supplier = supplier.clone();
    let control_supplier = supplier.clone();
    let health_supplier = supplier.clone();
    let card_origin = origin.to_owned();
    let control_host = origin.strip_prefix("http://").unwrap_or("").to_owned();
    Router::new()
        .route(
            "/schemas/wine-v1.schema.json",
            axum::routing::get(move || {
                let supplier = schema_supplier.clone();
                async move {
                if !supplier.structured.load(Ordering::Relaxed) {
                    return (StatusCode::NOT_FOUND, "Structured schema advertisement is disabled.").into_response();
                }
                (
                    [(axum::http::header::CONTENT_TYPE, "application/schema+json")],
                    include_str!("../schemas/wine-v1.schema.json"),
                ).into_response()
                }
            }),
        )
        .route("/demo/structures", axum::routing::post(move |headers: axum::http::HeaderMap, Json(change): Json<StructuredChange>| {
            let supplier = control_supplier.clone();
            let host = control_host.clone();
            async move {
                let Some(token) = &supplier.control_token else {
                    return (StatusCode::FORBIDDEN, "Supplier controls are not configured.").into_response();
                };
                let expected = format!("Bearer {token}");
                let actual = headers.get("authorization").and_then(|v| v.to_str().ok()).unwrap_or("");
                let equal = actual.len() == expected.len() &&
                    actual.bytes().zip(expected.bytes()).fold(0u8, |diff, (a, b)| diff | (a ^ b)) == 0;
                if !equal || headers.get("host").and_then(|v| v.to_str().ok()) != Some(host.as_str()) {
                    return (StatusCode::FORBIDDEN, "Invalid supplier control authorization or host.").into_response();
                }
                supplier.structured.store(change.enabled, Ordering::Relaxed);
                tracing::info!(enabled = change.enabled, "Supplier structured capability changed");
                Json(json!({"structuredDataParts": change.enabled})).into_response()
            }
        }))
        .nest(
            "/a2a",
            a2a_server::jsonrpc::jsonrpc_router(handler)
                .layer(middleware::from_fn(preserve_output_modes)),
        )
        .route("/.well-known/agent-card.json", axum::routing::get(move || {
            let supplier = card_supplier.clone();
            let origin = card_origin.clone();
            async move {
                ([(axum::http::header::CACHE_CONTROL, "no-store")],
                    Json(configured_card(&origin, supplier.structured.load(Ordering::Relaxed), supplier.inventory, supplier.advertised_slim_url())))
            }
        }))
        .route(
            "/health",
            axum::routing::get(move || {
                let supplier = health_supplier.clone();
                let metrics = metrics.clone();
                async move {
                Json(json!({
                    "status": "ok", "demoOnly": true,
                    "structuredRequests": metrics.structured_requests.load(Ordering::Relaxed),
                    "inferenceAttempts": metrics.inference_attempts.load(Ordering::Relaxed),
                    "structuredDataParts": supplier.structured.load(Ordering::Relaxed),
                    "oid4vp": if verifier_configured { "presentation-verifier-configured" } else { "verifier-not-configured" },
                    "slim": supplier.advertised_slim_url().unwrap_or(if supplier.slim_url.is_some() { "unavailable" } else { "disabled" })
                }))
                }
            }),
        )
        .layer(middleware::from_fn(reject_browser))
        .layer(middleware::from_fn(telemetry::http))
}

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct StructuredChange {
    enabled: bool,
}
