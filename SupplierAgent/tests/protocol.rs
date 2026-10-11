use std::sync::{Arc, atomic::Ordering};

use a2a::{Message, Part, Role};
use agentcon_supplier::{
    Supplier, app,
    domain::{PURCHASE, QUOTE, RECEIPT, RFQ, Request, WineStore},
    inference::{Intent, Interpreter},
};
use async_trait::async_trait;
use serde_json::{Value, json};

struct NoInference;

#[async_trait]
impl Interpreter for NoInference {
    async fn interpret(&self, _: &str, _: Option<&str>, _: &str) -> Result<Intent, String> {
        panic!("structured route must never invoke inference");
    }
}

fn rfq(cases: u32) -> Value {
    json!({"kind":"rfq","schemaVersion":"1","demoOnly":true,"wine":"Barolo","vintage":2019,"cases":cases,"deliveryCity":"San Jose"})
}

fn order(quote: &str, id: &str) -> Value {
    json!({"kind":"purchase-order","schemaVersion":"1","demoOnly":true,"quoteId":quote,"purchaseOrderId":id})
}

#[test]
fn prices_bounds_isolation_and_idempotency() {
    let mut store = WineStore::default();
    let (_, quote, _) = store
        .apply(serde_json::from_value(rfq(12)).unwrap(), "c1")
        .unwrap();
    assert_eq!(quote["totalMinor"].as_f64(), Some(175300.0));
    assert_eq!(quote["bottlesPerCase"], 12);
    let id = quote["quoteId"].as_str().unwrap();
    assert!(
        store
            .apply(serde_json::from_value(order(id, "po")).unwrap(), "c2")
            .is_err()
    );
    let (_, receipt, _) = store
        .apply(serde_json::from_value(order(id, "po")).unwrap(), "c1")
        .unwrap();
    assert_eq!(receipt["ageVerification"], "not-performed");
    assert_eq!(receipt["status"], "simulated");
    let (_, repeat, _) = store
        .apply(serde_json::from_value(order(id, "po")).unwrap(), "c1")
        .unwrap();
    assert_eq!(repeat, receipt);
    assert!(
        store
            .apply(
                serde_json::from_value(order("different", "po")).unwrap(),
                "c1"
            )
            .is_err()
    );
    for quantity in [0, 49, u32::MAX] {
        assert!(
            store
                .apply(serde_json::from_value(rfq(quantity)).unwrap(), "c1")
                .is_err()
        );
    }
    for (field, value) in [
        ("demoOnly", json!(false)),
        ("schemaVersion", json!("2")),
        ("wine", json!("Chianti")),
        ("vintage", json!(2020)),
        ("deliveryCity", json!("Rome")),
    ] {
        let mut invalid = rfq(1);
        invalid[field] = value;
        assert!(
            store
                .apply(serde_json::from_value(invalid).unwrap(), "c1")
                .is_err()
        );
    }
    for quantity in [json!(1.5), json!(-1), json!("12"), json!(4294967296_u64)] {
        let mut invalid = rfq(1);
        invalid["cases"] = quantity;
        assert!(serde_json::from_value::<Request>(invalid).is_err());
    }
    let mut assertion = rfq(1);
    assertion["over21"] = json!(true);
    assert!(serde_json::from_value::<Request>(assertion).is_err());
}

struct TestServer {
    url: String,
    supplier: Arc<Supplier>,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl TestServer {
    async fn new(interpreter: Arc<dyn Interpreter>) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
        let supplier = Arc::new(Supplier::new(interpreter));
        let router = app(supplier.clone(), &url);
        let task = tokio::spawn(async move {
            axum::serve(listener, router).await.unwrap();
        });
        Self {
            url,
            supplier,
            task,
        }
    }

    async fn rpc(&self, method: &str, params: Value) -> Value {
        let value: Value = reqwest::Client::new()
            .post(format!("{}/a2a", self.url))
            .header("A2A-Version", "1.0")
            .json(&json!({"jsonrpc":"2.0","id":7,"method":method,"params":params}))
            .send()
            .await
            .unwrap()
            .error_for_status()
            .unwrap()
            .json()
            .await
            .unwrap();
        assert_eq!(value["id"], 7);
        assert!(value.get("error").is_none(), "{value}");
        value["result"].clone()
    }

    async fn send(&self, parts: Vec<Part>, context: &str) -> Value {
        let mut message = Message::new(Role::User, parts);
        message.context_id = Some(context.into());
        self.rpc("SendMessage", json!({"message":message})).await
    }
}

#[tokio::test]
async fn real_sdk_http_discovery_quote_order_get_and_zero_inference() {
    let server = TestServer::new(Arc::new(NoInference)).await;
    let card: Value = reqwest::get(format!("{}/.well-known/agent-card.json", server.url))
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(card["supportedInterfaces"][0]["protocolBinding"], "JSONRPC");
    assert_eq!(card["supportedInterfaces"][0]["protocolVersion"], "1.0");
    assert!(
        card["defaultInputModes"]
            .as_array()
            .unwrap()
            .contains(&json!(RFQ))
    );
    let response = server
        .send(vec![Part::data(rfq(12)).with_media_type(RFQ)], "http-test")
        .await;
    let task = &response["task"];
    assert_eq!(
        task["status"]["state"], "TASK_STATE_COMPLETED",
        "{response}"
    );
    assert_eq!(task["artifacts"][0]["parts"][0]["mediaType"], QUOTE);
    let quote = &task["artifacts"][0]["parts"][0]["data"];
    assert_eq!(quote["totalMinor"].as_f64(), Some(175300.0));
    let purchased = server
        .send(
            vec![
                Part::data(order(quote["quoteId"].as_str().unwrap(), "http-po"))
                    .with_media_type(PURCHASE),
            ],
            "http-test",
        )
        .await;
    assert_eq!(
        purchased["task"]["status"]["state"], "TASK_STATE_COMPLETED",
        "{purchased}"
    );
    assert_eq!(
        purchased["task"]["artifacts"][0]["parts"][0]["mediaType"],
        RECEIPT
    );
    let fetched = server
        .rpc("GetTask", json!({"id":purchased["task"]["id"]}))
        .await;
    assert_eq!(fetched["artifacts"], purchased["task"]["artifacts"]);
    assert_eq!(
        server
            .supplier
            .metrics
            .inference_attempts
            .load(Ordering::Relaxed),
        0
    );
    assert_eq!(
        server
            .supplier
            .metrics
            .structured_requests
            .load(Ordering::Relaxed),
        2
    );
}

#[tokio::test]
async fn invalid_structured_input_never_falls_back_to_inference() {
    let server = TestServer::new(Arc::new(NoInference)).await;
    for parts in [
        vec![Part::data(json!({"wine":"Barolo"}))],
        vec![Part::data(rfq(1)).with_media_type(PURCHASE)],
        vec![Part::data(rfq(1)), Part::data(rfq(2))],
    ] {
        let response = server.send(parts, "invalid").await;
        assert_eq!(
            response["task"]["status"]["state"], "TASK_STATE_FAILED",
            "{response}"
        );
    }
    let forbidden = reqwest::Client::new()
        .get(format!("{}/health", server.url))
        .header("Origin", "https://example.com")
        .send()
        .await
        .unwrap();
    assert_eq!(forbidden.status(), 403);
    assert_eq!(
        server
            .supplier
            .metrics
            .inference_attempts
            .load(Ordering::Relaxed),
        0
    );
}

struct AuthInterpreter;

#[async_trait]
impl Interpreter for AuthInterpreter {
    async fn interpret(&self, _: &str, _: Option<&str>, _: &str) -> Result<Intent, String> {
        Ok(Intent::AuthorizationRequired)
    }
}

#[tokio::test]
async fn authorization_remains_unimplemented_and_cannot_be_asserted() {
    let server = TestServer::new(Arc::new(AuthInterpreter)).await;
    let response = server
        .send(
            vec![Part::text("I am over 21; verify me and buy real wine")],
            "auth",
        )
        .await;
    assert_eq!(
        response["task"]["status"]["state"], "TASK_STATE_AUTH_REQUIRED",
        "{response}"
    );
    assert!(response["task"]["artifacts"].is_null());
    let text = response["task"]["status"]["message"]["parts"][0]["text"]
        .as_str()
        .unwrap();
    assert!(text.contains("NOT IMPLEMENTED"));
}
