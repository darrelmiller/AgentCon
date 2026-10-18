//! Natural-language interpretation through an OpenAI-compatible chat completions endpoint, such as an Azure
//! OpenAI resource's `openai/v1` route. This is what containers use; it needs no GitHub Copilot runtime.

use std::time::Duration;

use async_trait::async_trait;
use serde_json::{Value, json};
use tracing::Instrument;

use crate::domain::Inventory;
use crate::inference::{count_call, instructions, prompt, Intent, Interpreter};

/// The model endpoint, configured from files so the key never sits in the environment:
/// `MODEL_API_KEY_FILE`, `MODEL_BASE_URL_FILE` and `MODEL_NAME`, all or none. An Azure OpenAI resource URL
/// (`https://<resource>.openai.azure.com/`) gets its OpenAI-compatible `openai/v1` route appended.
pub struct ModelProvider {
    base_url: String,
    api_key: String,
    model: String,
}

impl std::fmt::Debug for ModelProvider {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ModelProvider")
            .field("base_url", &self.base_url)
            .field("api_key", &"<redacted>")
            .field("model", &self.model)
            .finish()
    }
}

impl ModelProvider {
    /// `Ok(None)` when no endpoint is configured.
    pub fn from_env() -> Result<Option<Self>, String> {
        let key_file = std::env::var_os("MODEL_API_KEY_FILE");
        let url_file = std::env::var_os("MODEL_BASE_URL_FILE");
        let model = std::env::var("MODEL_NAME").ok().filter(|model| !model.trim().is_empty());
        let (key_file, url_file, model) = match (key_file, url_file, model) {
            (None, None, None) => return Ok(None),
            (Some(key), Some(url), Some(model)) => (key, url, model),
            _ => return Err("Set all of MODEL_API_KEY_FILE, MODEL_BASE_URL_FILE and MODEL_NAME, or none.".into()),
        };
        let read = |path: &std::ffi::OsStr, what: &str| {
            std::fs::read_to_string(path)
                .map(|text| text.trim().to_owned())
                .map_err(|e| format!("Cannot read {what} file {}: {e}", std::path::Path::new(path).display()))
        };
        let api_key = read(&key_file, "MODEL_API_KEY")?;
        let base = read(&url_file, "MODEL_BASE_URL")?;
        if api_key.is_empty() {
            return Err("MODEL_API_KEY_FILE is empty.".into());
        }
        // https only, except a local endpoint (tests, a model server on the same host).
        let loopback = ["http://localhost:", "http://127.0.0.1:", "http://[::1]:"].iter().any(|prefix| base.starts_with(prefix));
        if !base.starts_with("https://") && !loopback {
            return Err("MODEL_BASE_URL must be an https:// URL (http only on localhost).".into());
        }
        let base = base.trim_end_matches('/');
        let base_url = if base.ends_with("/v1") { base.to_owned() } else { format!("{base}/openai/v1") };
        Ok(Some(Self { base_url, api_key, model: model.trim().to_owned() }))
    }

    pub fn model(&self) -> &str {
        &self.model
    }
}

pub struct OpenAiInterpreter {
    provider: ModelProvider,
    instructions: String,
    http: reqwest::Client,
}

impl OpenAiInterpreter {
    pub fn new(provider: ModelProvider, inventory: &Inventory) -> Result<Self, String> {
        let http = reqwest::Client::builder()
            .timeout(Duration::from_secs(100))
            .build()
            .map_err(|e| format!("Cannot build the model HTTP client: {e}"))?;
        Ok(Self { provider, instructions: instructions(inventory), http })
    }
}

#[async_trait]
impl Interpreter for OpenAiInterpreter {
    fn provider(&self) -> &'static str {
        "openai"
    }

    async fn interpret(&self, text: &str, latest_quote: Option<&str>, context: &str) -> Result<Intent, String> {
        let model = &self.provider.model;
        let span = tracing::info_span!("chat", otel.kind = "client", gen_ai.operation.name = "chat",
            gen_ai.provider.name = "openai", gen_ai.request.model = %model,
            gen_ai.usage.input_tokens = tracing::field::Empty, gen_ai.usage.output_tokens = tracing::field::Empty);
        let body = json!({
            "model": model,
            "messages": [
                {"role": "system", "content": self.instructions},
                {"role": "user", "content": prompt(text, latest_quote, context)}
            ]
        });
        count_call();
        let started = std::time::Instant::now();
        let reply = async {
            // OpenAI reads a bearer token; Azure OpenAI reads the `api-key` header.
            let response = self.http.post(format!("{}/chat/completions", self.provider.base_url))
                .bearer_auth(&self.provider.api_key)
                .header("api-key", &self.provider.api_key)
                .json(&body)
                .send()
                .await
                .map_err(|e| format!("Model request failed; no order executed: {}", e.without_url()))?;
            let status = response.status();
            if !status.is_success() {
                // OpenAI and Azure OpenAI describe failures as {"error": {"code", "message"}}; neither echoes the key.
                let detail = response.json::<Value>().await.ok().and_then(|body| {
                    let error = body.get("error")?;
                    let code = error.get("code").and_then(Value::as_str).unwrap_or("");
                    let message = error.get("message").and_then(Value::as_str).unwrap_or("");
                    Some(format!(" ({code}{}{})", if code.is_empty() || message.is_empty() { "" } else { ": " },
                        message.chars().take(200).collect::<String>()))
                }).unwrap_or_default();
                return Err(format!("Model endpoint returned HTTP {status}{detail}; no order executed."));
            }
            response.json::<Value>().await.map_err(|e| format!("Model returned an unreadable response; no order executed: {}", e.without_url()))
        }
        .instrument(span.clone())
        .await
        .inspect_err(|_| crate::telemetry::error(&span, "model_request_failed"));
        crate::inference::record_duration(started.elapsed().as_secs_f64() * 1000.0);
        let reply: Value = reply?;
        for (field, name) in [("prompt_tokens", "gen_ai.usage.input_tokens"), ("completion_tokens", "gen_ai.usage.output_tokens")] {
            if let Some(tokens) = reply.pointer(&format!("/usage/{field}")).and_then(Value::as_i64) {
                span.record(name, tokens);
            }
        }
        let content = reply
            .pointer("/choices/0/message/content")
            .and_then(Value::as_str)
            .ok_or("Model returned no assistant content; no order executed.")?;
        parse_intent(content).inspect_err(|_| crate::telemetry::error(&span, "invalid_inference_result"))
    }
}

/// The model is told to return only a JSON object; tolerate a Markdown code fence around it.
fn parse_intent(content: &str) -> Result<Intent, String> {
    let trimmed = content.trim();
    let json = trimmed
        .strip_prefix("```json")
        .or_else(|| trimmed.strip_prefix("```"))
        .and_then(|rest| rest.strip_suffix("```"))
        .map_or(trimmed, str::trim);
    serde_json::from_str::<Intent>(json).map_err(|e| format!("Model returned an invalid intent; no order executed: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn model_requests_count_and_measure_responses_including_http_errors() {
        use std::sync::atomic::{AtomicU64, Ordering};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let origin = format!("http://{}", listener.local_addr().unwrap());
        let router = axum::Router::new()
            .route("/ok/chat/completions", axum::routing::post(|| async {
                tokio::time::sleep(Duration::from_millis(25)).await;
                axum::Json(json!({"choices":[{"message":{"content":"{\"action\":\"help\"}"}}]}))
            }))
            .route("/error/chat/completions", axum::routing::post(|| async {
                tokio::time::sleep(Duration::from_millis(25)).await;
                axum::http::StatusCode::BAD_GATEWAY
            }));
        let server = tokio::spawn(async move { axum::serve(listener, router).await.unwrap(); });
        let before = crate::inference::llm_snapshot();
        crate::inference::MESSAGE_LLM_CALLS.scope(AtomicU64::new(0), async {
            for endpoint in ["ok", "error"] {
                let interpreter = OpenAiInterpreter::new(ModelProvider {
                    base_url: format!("{origin}/{endpoint}"),
                    api_key: "test-only".into(),
                    model: "test-model".into(),
                }, &crate::domain::BAROLO).unwrap();
                let result = interpreter.interpret("help", None, "metrics-test").await;
                if endpoint == "ok" {
                    assert!(matches!(result, Ok(Intent::Help)));
                } else {
                    assert!(result.unwrap_err().contains("502"));
                }
            }
            assert_eq!(crate::inference::MESSAGE_LLM_CALLS.with(|calls| calls.load(Ordering::Relaxed)), 2);
        }).await;
        let after = crate::inference::llm_snapshot();
        assert!(after["calls"].as_u64().unwrap() >= before["calls"].as_u64().unwrap() + 2);
        assert!(after["timedCalls"].as_u64().unwrap() >= before["timedCalls"].as_u64().unwrap() + 2);
        assert!(after["responseTimeMs"].as_f64().unwrap() - before["responseTimeMs"].as_f64().unwrap() >= 50.0);
        server.abort();
    }

    #[test]
    fn parses_plain_and_fenced_intents() {
        assert!(matches!(parse_intent(r#"{"action":"help"}"#), Ok(Intent::Help)));
        assert!(matches!(parse_intent("```json\n{\"action\":\"help\"}\n```"), Ok(Intent::Help)));
        assert!(parse_intent("not json").is_err());
    }
}
