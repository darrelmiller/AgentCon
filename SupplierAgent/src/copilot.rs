//! The GitHub Copilot SDK interpreter (cargo feature `copilot`, on by default): used on laptops, where the
//! developer's Copilot login provides the model. Containers use the OpenAI-compatible client instead.

use std::{path::PathBuf, sync::Arc, time::Duration};

use async_trait::async_trait;
use futures::{FutureExt, StreamExt};
use github_copilot_sdk::{
    Client, ClientMode, ClientOptions, MemoryConfiguration, MessageOptions, SessionConfig,
    SystemMessageConfig, handler::DenyAllHandler,
};
use tokio::sync::Mutex;
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;

use crate::domain::Inventory;
use crate::inference::{instructions, prompt, Intent, Interpreter, MESSAGE_LLM_COMPLETE};

fn incomplete_calls() {
    crate::inference::incomplete_metrics();
    let _ = MESSAGE_LLM_COMPLETE
        .try_with(|complete| complete.store(false, std::sync::atomic::Ordering::Relaxed));
    tracing::warn!(
        "Copilot event subscription lagged; message LLM count and usage may be incomplete"
    );
}

fn record_call(value: &serde_json::Value) {
    if matches!(
        value.get("type").and_then(|v| v.as_str()),
        Some("assistant.turn_start" | "assistant.turn_retry")
    ) {
        crate::inference::count_call();
    }
    if value.get("type").and_then(|v| v.as_str()) == Some("assistant.usage") {
        if let Some(milliseconds) = value.pointer("/data/duration").and_then(|v| v.as_f64()) {
            crate::inference::record_duration(milliseconds);
        }
    }
}

#[cfg(test)]
mod counter_tests {
    use crate::inference::MESSAGE_LLM_CALLS;
    #[tokio::test]
    async fn counts_actual_sdk_turns_and_retries_not_usage_or_payload_events() {
        use std::sync::atomic::{AtomicU64, Ordering};
        MESSAGE_LLM_CALLS
            .scope(AtomicU64::new(0), async {
                for event in [
                    "assistant.turn_start",
                    "assistant.message",
                    "assistant.usage",
                    "assistant.turn_retry",
                ] {
                    super::record_call(&serde_json::json!({"type":event}));
                }
                assert_eq!(
                    MESSAGE_LLM_CALLS.with(|calls| calls.load(Ordering::Relaxed)),
                    2
                );
            })
            .await;
        MESSAGE_LLM_CALLS
            .scope(AtomicU64::new(0), async {
                assert_eq!(
                    MESSAGE_LLM_CALLS.with(|calls| calls.load(Ordering::Relaxed)),
                    0
                );
            })
            .await;
    }
}

pub struct CopilotInterpreter {
    client: Mutex<Option<Client>>,
    state_dir: PathBuf,
    model: String,
    instructions: String,
}

struct SessionCleanup {
    session: Option<Arc<github_copilot_sdk::session::Session>>,
    parent: opentelemetry::Context,
}

impl Drop for SessionCleanup {
    fn drop(&mut self) {
        if let Some(session) = self.session.take() {
            let span = tracing::info_span!("copilot.cancel_cleanup");
            let _ = span.set_parent(self.parent.clone());
            crate::telemetry::error(&span, "canceled");
            tokio::spawn(
                async move {
                    if session.abort().await.is_err() {
                        tracing::error!("Copilot cancellation abort failed");
                    }
                    if session.disconnect().await.is_err() {
                        tracing::error!("Copilot cancellation disconnect failed");
                    }
                }
                .instrument(span),
            );
        }
    }
}

impl CopilotInterpreter {
    pub fn new(state_dir: PathBuf, model: String, inventory: &Inventory) -> Self {
        Self {
            client: Mutex::new(None),
            state_dir,
            model,
            instructions: instructions(inventory),
        }
    }

    pub async fn stop(&self) -> Result<(), String> {
        if let Some(client) = self.client.lock().await.take() {
            client.stop().await.map_err(|e| e.to_string())?;
        }
        Ok(())
    }
}

#[async_trait]
impl Interpreter for CopilotInterpreter {
    async fn interpret(
        &self,
        text: &str,
        latest_quote: Option<&str>,
        context: &str,
    ) -> Result<Intent, String> {
        let mut guard = self.client.lock().await;
        if guard.is_none() {
            tokio::fs::create_dir_all(&self.state_dir)
                .await
                .map_err(|e| e.to_string())?;
            let mut options = ClientOptions::new()
                .with_mode(ClientMode::Empty)
                .with_base_directory(&self.state_dir)
                .with_use_logged_in_user(true);
            options.working_directory = self.state_dir.clone();
            options
                .env
                .push(("COPILOT_OTEL_ENABLED".into(), "false".into()));
            options.env.push((
                "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT".into(),
                "false".into(),
            ));
            options
                .env
                .push(("SUPPLIER_VERIFIER_TOKEN".into(), "".into()));
            options
                .env
                .push(("SUPPLIER_DEMO_CONTROL_TOKEN".into(), "".into()));
            let client = Client::start(options)
                .instrument(crate::telemetry::runtime_start())
                .await
                .map_err(|e| format!("Copilot startup/authentication failed: {e}"))?;
            *guard = Some(client);
        }
        let client = guard
            .as_ref()
            .ok_or("Copilot client missing after startup")?;
        let config = SessionConfig::default()
            .with_model(&self.model)
            .with_permission_handler(Arc::new(DenyAllHandler))
            .with_available_tools(Vec::<String>::new())
            .with_memory(MemoryConfiguration::disabled())
            .with_enable_session_store(false)
            .with_skip_custom_instructions(true)
            .with_working_directory(&self.state_dir)
            .with_system_message(
                SystemMessageConfig::new()
                    .with_mode("replace")
                    .with_content(&self.instructions),
            );
        let session = Arc::new(
            client
                .create_session(config)
                .await
                .map_err(|e| e.to_string())?,
        );
        let mut cleanup = SessionCleanup {
            session: Some(session.clone()),
            parent: tracing::Span::current().context(),
        };
        let prompt = prompt(text, latest_quote, context);
        let span = tracing::info_span!("copilot.agent_loop");
        let parent = span.context();
        let mut events = session.subscribe();
        let result = async {
            let send = session.send_and_wait(
                MessageOptions::new(prompt).with_wait_timeout(Duration::from_secs(100)),
            );
            tokio::pin!(send);
            loop {
                tokio::select! {
                    result = &mut send => break result,
                    Some(event) = events.next() => match event {
                        Ok(event) => if let Ok(value) = serde_json::to_value(event) {
                            record_call(&value);
                            crate::telemetry::usage(&value, &parent, "SupplierAgent");
                        },
                        Err(_) => incomplete_calls(),
                    }
                }
            }
        }
        .instrument(span.clone())
        .await;
        while let Some(Some(event)) = events.next().now_or_never() {
            if let Ok(event) = event {
                if let Ok(value) = serde_json::to_value(event) {
                    record_call(&value);
                    crate::telemetry::usage(&value, &parent, "SupplierAgent");
                }
            } else {
                incomplete_calls();
            }
        }
        if result.is_err() {
            crate::telemetry::error(&span, "copilot_inference_failed");
            if session.abort().await.is_err() {
                tracing::error!("Copilot abort failed");
            }
        }
        let parsed = match result {
            Ok(Some(event)) => (|| {
                let value = serde_json::to_value(&event).map_err(|e| e.to_string())?;
                let content = value
                    .get("data")
                    .and_then(|d| d.get("content"))
                    .and_then(|v| v.as_str())
                    .ok_or("Copilot returned no assistant content")?;
                serde_json::from_str::<Intent>(content).map_err(|e| {
                    format!("Copilot returned an invalid intent; no order executed: {e}")
                })
            })(),
            Ok(None) => Err("Copilot returned no final response; no order executed.".into()),
            Err(e) => Err(format!("Copilot inference failed; no order executed: {e}")),
        };
        if parsed.is_err() {
            crate::telemetry::error(&span, "invalid_inference_result");
        }
        session
            .disconnect()
            .await
            .map_err(|e| format!("Copilot session cleanup failed: {e}"))?;
        cleanup.session = None;
        parsed
    }
}
