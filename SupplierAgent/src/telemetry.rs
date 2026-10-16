use std::{
    collections::HashMap,
    time::{Duration, SystemTime},
};

use axum::{extract::Request, middleware::Next, response::Response};
use opentelemetry::{
    Context, KeyValue, global,
    propagation::{Extractor, Injector},
    trace::{Span, SpanKind, Status, Tracer, TracerProvider},
};
use opentelemetry_sdk::{Resource, propagation::TraceContextPropagator, trace::SdkTracerProvider};
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;
use tracing_subscriber::{Layer, layer::SubscriberExt, util::SubscriberInitExt};

pub fn init() -> Result<SdkTracerProvider, Box<dyn std::error::Error>> {
    global::set_text_map_propagator(TraceContextPropagator::new());
    let resource = Resource::builder()
        .with_service_name(
            std::env::var("OTEL_SERVICE_NAME").unwrap_or_else(|_| "agentcon-supplier".into()),
        )
        .with_attribute(KeyValue::new("service.version", env!("CARGO_PKG_VERSION")))
        .build();
    let mut builder = SdkTracerProvider::builder().with_resource(resource);
    if std::env::var("OTEL_EXPORTER_OTLP_ENDPOINT")
        .ok()
        .filter(|s| !s.trim().is_empty())
        .is_some()
        || std::env::var("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")
            .ok()
            .filter(|s| !s.trim().is_empty())
            .is_some()
    {
        let protocol = std::env::var("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL")
            .or_else(|_| std::env::var("OTEL_EXPORTER_OTLP_PROTOCOL"))
            .unwrap_or_else(|_| "grpc".into());
        let exporter = match protocol.as_str() {
            "grpc" => opentelemetry_otlp::SpanExporter::builder()
                .with_tonic()
                .build()?,
            "http/protobuf" => opentelemetry_otlp::SpanExporter::builder()
                .with_http()
                .build()?,
            _ => return Err("Supported OTLP protocols: grpc, http/protobuf".into()),
        };
        builder = builder.with_batch_exporter(exporter);
    } else {
        eprintln!(
            "Supplier OTLP export deliberately disabled: no OTEL_EXPORTER_OTLP_ENDPOINT configured."
        );
    }
    let provider = builder.build();
    global::set_tracer_provider(provider.clone());
    // Keep SDK/exporter warnings observable; never export log payloads as trace events.
    tracing_subscriber::registry()
        .with(
            tracing_subscriber::fmt::layer().with_filter(
                tracing_subscriber::EnvFilter::try_from_default_env()
                    .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
            ),
        )
        .with(
            tracing_opentelemetry::layer()
                .with_tracer(provider.tracer("AgentCon.Supplier"))
                .with_error_events_to_status(false)
                .with_error_events_to_exceptions(false)
                .with_filter(tracing_subscriber::filter::filter_fn(|metadata| {
                    metadata.is_span() && metadata.target().starts_with("agentcon_supplier")
                })),
        )
        .init();
    Ok(provider)
}

struct Headers<'a>(&'a axum::http::HeaderMap);
impl Extractor for Headers<'_> {
    fn get(&self, key: &str) -> Option<&str> {
        self.0.get(key)?.to_str().ok()
    }
    fn keys(&self) -> Vec<&str> {
        self.0.keys().map(|k| k.as_str()).collect()
    }
}
struct HeaderWriter<'a>(&'a mut axum::http::HeaderMap);
impl Injector for HeaderWriter<'_> {
    fn set(&mut self, key: &str, value: String) {
        if let (Ok(key), Ok(value)) = (axum::http::HeaderName::try_from(key), value.parse()) {
            self.0.insert(key, value);
        }
    }
}
struct Params<'a>(&'a HashMap<String, Vec<String>>);
impl Extractor for Params<'_> {
    fn get(&self, key: &str) -> Option<&str> {
        self.0.get(key)?.first().map(String::as_str)
    }
    fn keys(&self) -> Vec<&str> {
        self.0.keys().map(String::as_str).collect()
    }
}

pub fn executor_parent(params: &HashMap<String, Vec<String>>) -> Context {
    global::get_text_map_propagator(|p| p.extract(&Params(params)))
}

pub fn runtime_start() -> tracing::Span {
    // SDK background readers retain the current tracing span for their lifetime.
    // Keep process lifetime work off request spans and outside the exported target.
    tracing::info_span!(target: "agentcon_runtime", parent: None, "copilot.runtime_start")
}

pub async fn http(mut request: Request, next: Next) -> Response {
    let parent = global::get_text_map_propagator(|p| p.extract(&Headers(request.headers())));
    let route = match request.uri().path() {
        "/a2a" | "/a2a/" => "/a2a",
        "/health" => "/health",
        "/.well-known/agent-card.json" => "/.well-known/agent-card.json",
        _ => "unmatched",
    };
    let span = tracing::info_span!("http.request", otel.name = %format!("{} {route}", request.method()),
        otel.kind = "server", http.request.method = %request.method(),
        http.route = route, http.response.status_code = tracing::field::Empty,
        otel.status_code = tracing::field::Empty, error.type = tracing::field::Empty);
    let _ = span.set_parent(parent);
    // The pinned A2A SDK copies headers to ExecutorContext.service_params before
    // tokio::spawn. Replace traceparent with THIS server span's context so its
    // background executor remains a child even after the HTTP response finishes.
    global::get_text_map_propagator(|p| {
        p.inject_context(&span.context(), &mut HeaderWriter(request.headers_mut()))
    });
    let response = next.run(request).instrument(span.clone()).await;
    span.record("http.response.status_code", response.status().as_u16());
    if response.status().is_client_error() || response.status().is_server_error() {
        span.record("otel.status_code", "ERROR");
        span.record("error.type", response.status().as_str());
    }
    response
}

pub fn error(span: &tracing::Span, code: &'static str) {
    span.set_status(Status::error(""));
    span.set_attribute("error.type", code);
}

pub fn usage(evt: &serde_json::Value, parent: &Context, agent: &'static str) {
    if evt.get("type").and_then(|v| v.as_str()) != Some("assistant.usage") {
        return;
    }
    let Some(data) = evt.get("data") else { return };
    let model = data.get("model").and_then(|v| v.as_str());
    let end = evt
        .get("timestamp")
        .and_then(|v| v.as_str())
        .and_then(|t| chrono::DateTime::parse_from_rfc3339(t).ok())
        .map(SystemTime::from)
        .unwrap_or_else(SystemTime::now);
    let start = data
        .get("duration")
        .and_then(|v| v.as_u64())
        .and_then(|ms| end.checked_sub(Duration::from_millis(ms)))
        .unwrap_or(end);
    let tracer = global::tracer("AgentCon.Supplier");
    let mut attrs = vec![
        KeyValue::new("gen_ai.operation.name", "chat"),
        KeyValue::new("gen_ai.agent.name", agent),
        KeyValue::new("gen_ai.provider.name", "github.copilot"),
        KeyValue::new("agentcon.inference.observation", "sdk.assistant.usage"),
    ];
    if let Some(model) = model {
        attrs.push(KeyValue::new("gen_ai.request.model", model.to_owned()));
        attrs.push(KeyValue::new("gen_ai.response.model", model.to_owned()));
    }
    for (field, attr) in [
        ("inputTokens", "gen_ai.usage.input_tokens"),
        ("outputTokens", "gen_ai.usage.output_tokens"),
        ("cacheReadTokens", "gen_ai.usage.cache_read.input_tokens"),
        ("cacheWriteTokens", "gen_ai.usage.cache_write.input_tokens"),
        ("reasoningTokens", "gen_ai.usage.reasoning.output_tokens"),
    ] {
        if let Some(tokens) = data.get(field).and_then(|v| v.as_i64()).filter(|n| *n >= 0) {
            attrs.push(KeyValue::new(attr, tokens));
        }
    }
    if let Some(reason) = data.get("finishReason").and_then(|v| v.as_str()) {
        attrs.push(KeyValue::new(
            "gen_ai.response.finish_reasons",
            opentelemetry::Value::Array(opentelemetry::Array::String(vec![
                reason.to_owned().into(),
            ])),
        ));
    }
    let mut span = tracer
        .span_builder(
            model
                .map(|m| format!("chat {m}"))
                .unwrap_or_else(|| "chat".into()),
        )
        .with_kind(SpanKind::Client)
        .with_start_time(start)
        .with_attributes(attrs)
        .start_with_context(&tracer, parent);
    if data.get("finishReason").and_then(|v| v.as_str()) == Some("error") {
        span.set_status(Status::error(""));
        span.set_attribute(KeyValue::new("error.type", "inference_error"));
    }
    span.end_with_timestamp(end);
}
