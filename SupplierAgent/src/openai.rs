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
        let reply: Value = async {
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
                return Err(format!("Model endpoint returned HTTP {status}; no order executed."));
            }
            response.json::<Value>().await.map_err(|e| format!("Model returned an unreadable response; no order executed: {}", e.without_url()))
        }
        .instrument(span.clone())
        .await
        .inspect_err(|_| crate::telemetry::error(&span, "model_request_failed"))?;
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

    #[test]
    fn parses_plain_and_fenced_intents() {
        assert!(matches!(parse_intent(r#"{"action":"help"}"#), Ok(Intent::Help)));
        assert!(matches!(parse_intent("```json\n{\"action\":\"help\"}\n```"), Ok(Intent::Help)));
        assert!(parse_intent("not json").is_err());
    }
}
