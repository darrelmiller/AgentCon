use std::sync::Arc;

use agentcon_supplier::{
    Supplier, app,
    inference::{Intent, Interpreter},
    telemetry,
};
use async_trait::async_trait;
use opentelemetry::{
    global,
    trace::{SpanKind, Status, TracerProvider},
};
use opentelemetry_sdk::{
    propagation::TraceContextPropagator,
    trace::{InMemorySpanExporter, SdkTracerProvider},
};
use serde_json::json;
use tracing_subscriber::{layer::SubscriberExt, util::SubscriberInitExt};

struct NoInference;
#[async_trait]
impl Interpreter for NoInference {
    async fn interpret(&self, _: &str, _: Option<&str>, _: &str) -> Result<Intent, String> {
        panic!("structured tracing must not call inference")
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn sdk_background_executor_is_child_of_http_and_preserves_remote_trace() {
    global::set_text_map_propagator(TraceContextPropagator::new());
    let exporter = InMemorySpanExporter::default();
    let provider = SdkTracerProvider::builder()
        .with_simple_exporter(exporter.clone())
        .build();
    global::set_tracer_provider(provider.clone());
    tracing_subscriber::registry()
        .with(tracing_opentelemetry::layer().with_tracer(provider.tracer("test")))
        .init();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
    let router = app(Arc::new(Supplier::new(Arc::new(NoInference))), &url);
    let server = tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    for cases in [12, 0] {
        let result: serde_json::Value = reqwest::Client::new().post(format!("{url}/a2a"))
            .header("traceparent", "00-12345678901234567890123456789012-1234567890123456-01")
            .header("A2A-Version", "1.0")
            .json(&json!({"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{
                "message":{"messageId":format!("m-{cases}"),"role":"ROLE_USER","parts":[{
                    "data":{"kind":"rfq","schemaVersion":"1","demoOnly":true,"wine":"Barolo","vintage":2019,"cases":cases,"deliveryCity":"San Jose"},
                    "mediaType":"application/vnd.agentcon.demo.wine-rfq.v1+json"}]},
                "configuration":{"returnImmediately":false}}}))
            .send().await.unwrap().json().await.unwrap();
        assert!(result.get("error").is_none(), "{result}");
    }
    provider.force_flush().unwrap();
    let spans = exporter.get_finished_spans().unwrap();
    let agents: Vec<_> = spans
        .iter()
        .filter(|s| s.name == "invoke_agent SupplierAgent")
        .collect();
    assert_eq!(agents.len(), 2);
    for agent in &agents {
        assert_eq!(agent.span_kind, SpanKind::Internal);
        assert_eq!(
            agent.span_context.trace_id().to_string(),
            "12345678901234567890123456789012"
        );
        let http = spans
            .iter()
            .find(|s| s.span_context.span_id() == agent.parent_span_id)
            .unwrap();
        assert_eq!(http.span_kind, SpanKind::Server);
        assert_eq!(http.parent_span_id.to_string(), "1234567890123456");
        assert!(agent.attributes.iter().any(
            |a| a.key.as_str() == "gen_ai.operation.name" && a.value.as_str() == "invoke_agent"
        ));
        assert!(
            agent
                .attributes
                .iter()
                .any(|a| a.key.as_str() == "agentcon.a2a.task.id")
        );
    }
    assert_eq!(
        agents
            .iter()
            .filter(|s| matches!(s.status, Status::Error { .. }))
            .count(),
        1
    );
    assert!(!spans.iter().any(|s| s.name.starts_with("chat")));
    assert!(
        !spans
            .iter()
            .flat_map(|s| &s.attributes)
            .any(|a| a.key.as_str().contains("input_tokens"))
    );

    let parent = telemetry::executor_parent(&std::collections::HashMap::from([(
        "traceparent".into(),
        vec!["00-12345678901234567890123456789012-1234567890123456-01".into()],
    )]));
    let fixture = json!({"id":"usage-test","parentId":null,"type":"assistant.usage","timestamp":"2026-10-10T20:00:00Z",
        "data":{"model":"fixture-model","duration":20,"inputTokens":13,"outputTokens":4,"content":"PRIVATE"}});
    let sdk_event: github_copilot_sdk::SessionEvent = serde_json::from_value(fixture).unwrap();
    telemetry::usage(
        &serde_json::to_value(sdk_event).unwrap(),
        &parent,
        "SupplierAgent",
    );
    let spans = exporter.get_finished_spans().unwrap();
    let chat = spans
        .iter()
        .find(|s| s.name == "chat fixture-model")
        .unwrap();
    assert_eq!(chat.span_kind, SpanKind::Client);
    assert_eq!(
        chat.span_context.trace_id().to_string(),
        "12345678901234567890123456789012"
    );
    assert!(
        chat.attributes
            .iter()
            .any(|a| a.key.as_str() == "gen_ai.usage.input_tokens" && a.value.to_string() == "13")
    );
    assert!(
        !chat
            .attributes
            .iter()
            .any(|a| a.value.to_string().contains("PRIVATE"))
    );
    server.abort();
    provider.shutdown().unwrap();
}
