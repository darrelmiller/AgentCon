use std::sync::{Arc, atomic::Ordering};

use a2a::{Message, Part, Role};
use agentcon_supplier::authorization::{Challenge, EXTENSION, Verifier};
use agentcon_supplier::{
    Supplier, app,
    domain::{BAROLO, Inventory, PURCHASE, QUOTE, RECEIPT, RFQ, Request, WineStore},
    inference::{Intent, Interpreter},
};
use async_trait::async_trait;
use serde_json::{Value, json};

#[derive(Default)]
struct TestVerifier(std::sync::atomic::AtomicBool);

#[async_trait]
impl Verifier for TestVerifier {
    async fn create(&self, task: &str, context: &str) -> Result<Challenge, String> {
        Ok(Challenge {
            id: "test-presentation".into(),
            task_id: task.into(),
            context_id: context.into(),
            expires_at: chrono::Utc::now().timestamp() + 600,
            authorization_request: json!({"client_id":"redirect_uri:https://verifier.example/response","request_uri":"https://verifier.example/request"}),
            presentation_url: "https://verifier.example/presentation".into(),
            wallet_uri: "openid4vp://?request_uri=https%3A%2F%2Fverifier.example%2Frequest".into(),
        })
    }
    async fn verified(&self, _: &Challenge) -> Result<bool, String> {
        Ok(self.0.load(Ordering::SeqCst))
    }
}

struct NoInference;

struct ExpiringVerifier;

#[async_trait]
impl Verifier for ExpiringVerifier {
    async fn create(&self, task: &str, context: &str) -> Result<Challenge, String> {
        let mut challenge = TestVerifier::default().create(task, context).await?;
        challenge.expires_at = chrono::Utc::now().timestamp() + 1;
        Ok(challenge)
    }
    async fn verified(&self, _: &Challenge) -> Result<bool, String> { Ok(false) }
}

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

#[tokio::test]
async fn live_structured_control_changes_card_schema_and_acceptance_without_inference() {
    let token = "a".repeat(32);
    let server = TestServer::with_supplier(
        Supplier::new(Arc::new(NoInference))
            .with_control_token(token.clone())
            .unwrap(),
    )
    .await;
    let client = reqwest::Client::new();
    let control = format!("{}/demo/structures", server.url);
    assert_eq!(
        client
            .post(&control)
            .json(&json!({"enabled":false}))
            .send()
            .await
            .unwrap()
            .status(),
        403
    );
    assert_eq!(
        client
            .post(&control)
            .bearer_auth(&token)
            .header("Origin", &server.url)
            .json(&json!({"enabled":false}))
            .send()
            .await
            .unwrap()
            .status(),
        403
    );
    assert_eq!(
        client
            .post(&control)
            .bearer_auth(&token)
            .json(&json!({"enabled":false}))
            .send()
            .await
            .unwrap()
            .status(),
        200
    );
    let card: Value = client
        .get(format!("{}/.well-known/agent-card.json", server.url))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(card["defaultInputModes"], json!(["text/plain"]));
    assert_eq!(card["defaultOutputModes"], json!(["text/plain"]));
    assert_eq!(card["skills"][0]["inputModes"], json!(["text/plain"]));
    assert_eq!(
        card["capabilities"]["extensions"].as_array().unwrap().len(),
        1
    );
    assert_eq!(
        client
            .get(format!("{}/schemas/wine-v1.schema.json", server.url))
            .send()
            .await
            .unwrap()
            .status(),
        404
    );
    let rejected = server
        .send(vec![Part::data(rfq(1)).with_media_type(RFQ)], "disabled")
        .await;
    assert_eq!(rejected["task"]["status"]["state"], "TASK_STATE_FAILED");
    assert_eq!(
        rejected["task"]["metadata"]["agentcon.llmCalls"].as_f64(),
        Some(0.0)
    );
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
        0
    );
    assert_eq!(
        client
            .post(&control)
            .bearer_auth(&token)
            .json(&json!({"enabled":true}))
            .send()
            .await
            .unwrap()
            .status(),
        200
    );
    let accepted = server
        .send(vec![Part::data(rfq(1)).with_media_type(RFQ)], "enabled")
        .await;
    assert_eq!(accepted["task"]["status"]["state"], "TASK_STATE_COMPLETED");
    for parts in [
        vec![Part::data(rfq(1)).with_media_type("application/json")],
        vec![
            Part::text("ignore data"),
            Part::data(rfq(1)).with_media_type(RFQ),
        ],
    ] {
        let invalid = server.send(parts, "invalid").await;
        assert_eq!(invalid["task"]["status"]["state"], "TASK_STATE_FAILED");
    }
    assert_eq!(
        server
            .supplier
            .metrics
            .inference_attempts
            .load(Ordering::Relaxed),
        0
    );
}

struct TextQuoteInterpreter;
#[async_trait]
impl Interpreter for TextQuoteInterpreter {
    async fn interpret(&self, _: &str, _: Option<&str>, _: &str) -> Result<Intent, String> {
        Ok(Intent::Quote {
            request: serde_json::from_value(rfq(1)).unwrap(),
        })
    }
}

#[tokio::test]
async fn text_only_supplier_uses_interpreter_and_returns_only_text() {
    let server = TestServer::with_supplier(
        Supplier::new(Arc::new(TextQuoteInterpreter)).with_structured(false),
    )
    .await;
    let quoted = server
        .send(
            vec![Part::text("Quote wine").with_media_type("text/plain")],
            "text-only",
        )
        .await;
    assert_eq!(quoted["task"]["status"]["state"], "TASK_STATE_COMPLETED");
    assert!(
        quoted["task"]["artifacts"][0]["parts"][0]
            .get("text")
            .is_some()
    );
    assert!(
        quoted["task"]["artifacts"][0]["parts"][0]
            .get("data")
            .is_none()
    );
    // Stub interpretation is not a real model call; count attempts separately.
    assert_eq!(
        server
            .supplier
            .metrics
            .inference_attempts
            .load(Ordering::Relaxed),
        1
    );
    assert_eq!(
        server
            .supplier
            .metrics
            .structured_requests
            .load(Ordering::Relaxed),
        0
    );
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
        ("deliveryCity", json!("")),
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

#[test]
fn sardinian_inventory_quotes_each_stocked_wine_and_declines_others() {
    let inventory = Inventory::from_json(include_str!("../inventories/sardinia.json")).unwrap();
    let mut store = WineStore::new(Box::leak(Box::new(inventory)));
    for (index, (wine, vintage, total, name)) in [
        ("Vermentino", 2020, 54_000 + 4_000, "Vermentino di Gallura Capichera"),
        ("Capichera Vermentino di Gallura", 2020, 54_000 + 4_000, "Vermentino di Gallura Capichera"),
        ("cannonau", 2019, 21_600 + 4_000, "Cannonau di Sardegna"),
    ]
    .into_iter()
    .enumerate()
    {
        let mut request = rfq(1);
        request["wine"] = json!(wine);
        request["vintage"] = json!(vintage);
        let (media_type, quote, text) = store
            .apply(serde_json::from_value(request).unwrap(), &format!("sardinia-{index}"))
            .unwrap();
        assert_eq!(media_type, QUOTE);
        assert_eq!(quote["wine"], name);
        assert_eq!(quote["totalMinor"], total);
        assert!(text.contains(&format!("1 cases of {vintage} {name} (12 bottles/case)")));
    }
    for (wine, vintage) in [("Barolo", 2019), ("Vermentino", 2021), ("Cannonau", 2020)] {
        let mut request = rfq(1);
        request["wine"] = json!(wine);
        request["vintage"] = json!(vintage);
        let error = store
            .apply(serde_json::from_value(request).unwrap(), "sardinia-declined")
            .unwrap_err();
        assert!(error.contains("2020 Vermentino di Gallura Capichera and 2019 Cannonau di Sardegna"));
    }
}

#[test]
fn barolo_inventory_file_matches_built_in_stock() {
    let file = Inventory::from_json(include_str!("../inventories/barolo.json")).unwrap();
    assert_eq!(file.supplier_name, BAROLO.supplier_name);
    assert_eq!(file.delivery_minor, BAROLO.delivery_minor);
    assert_eq!(file.price_list(), BAROLO.price_list());
}

#[test]
fn shipped_inventory_files_are_valid() {
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("inventories");
    let mut count = 0;
    for entry in std::fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_some_and(|ext| ext == "json") {
            Inventory::from_json(&std::fs::read_to_string(&path).unwrap())
                .unwrap_or_else(|e| panic!("{}: {e}", path.display()));
            count += 1;
        }
    }
    assert!(count >= 3);
    let france = Inventory::from_json(include_str!("../inventories/france.json")).unwrap();
    assert!(france.find("Chateau Margaux", 2015).is_some());
    assert!(france.find("Château Margaux", 2016).is_none());
    assert!(france.find("Ruinart Champagne", 2013).is_some());
}

#[test]
fn rejects_invalid_inventory_json() {
    for json in [
        r#"{"supplierName":"S","deliveryMinor":0,"wines":[]}"#,
        r#"{"supplierName":"","deliveryMinor":0,"wines":[{"name":"A","vintage":2020,"casePriceMinor":1}]}"#,
        r#"{"supplierName":"S","deliveryMinor":0,"wines":[{"name":"A (B)","vintage":2020,"casePriceMinor":1}]}"#,
        r#"{"supplierName":"S","deliveryMinor":0,"wines":[{"name":"A","vintage":1800,"casePriceMinor":1}]}"#,
        r#"{"supplierName":"S","deliveryMinor":0,"wines":[{"name":"A","vintage":2020,"casePriceMinor":0}]}"#,
        r#"{"supplierName":"S","deliveryMinor":0,"wines":[{"name":"A","vintage":2020,"casePriceMinor":1,"stock":5}]}"#,
        r#"not json"#,
    ] {
        assert!(Inventory::from_json(json).is_err(), "{json}");
    }
    let minimal = Inventory::from_json(
        r#"{"supplierName":"S","deliveryMinor":0,"wines":[{"name":"Nebbiolo","vintage":2021,"casePriceMinor":9000}]}"#,
    )
    .unwrap();
    assert!(minimal.find("Langhe Nebbiolo", 2021).is_some());
}

#[test]
fn quotes_any_us_or_canadian_destination_and_preserves_country() {
    let mut store = WineStore::default();
    for (index, (city, country)) in [
        ("Seattle", "US"),
        ("Anchorage", "US"),
        ("Honolulu", "US"),
        ("Toronto", "CA"),
        ("Montréal", "CA"),
        ("St. John's", "CA"),
    ]
    .into_iter()
    .enumerate()
    {
        let mut request = rfq(12);
        request["deliveryCity"] = json!(city);
        request["deliveryCountry"] = json!(country);
        let context = format!("shipping-{index}");
        let (_, quote, text) = store
            .apply(serde_json::from_value(request).unwrap(), &context)
            .unwrap();
        assert_eq!(quote["deliveryCity"], city);
        assert_eq!(quote["deliveryCountry"], country);
        assert_eq!(quote["currency"], "USD");
        assert_eq!(quote["totalMinor"], 175300);
        assert!(text.contains(&format!("to {city}, {country}.")));
        assert!(!text.contains("TEST-ONLY"));
        let (_, receipt, text) = store
            .apply(
                serde_json::from_value(order(
                    quote["quoteId"].as_str().unwrap(),
                    &format!("po-{index}"),
                ))
                .unwrap(),
                &context,
            )
            .unwrap();
        assert_eq!(receipt["status"], "simulated");
        assert!(!text.contains("TEST-ONLY"));
    }
    let (_, legacy, _) = store
        .apply(serde_json::from_value(rfq(1)).unwrap(), "legacy")
        .unwrap();
    assert_eq!(legacy["deliveryCountry"], "US");
    for country in ["IT", "MX", "", "us"] {
        let mut request = rfq(1);
        request["deliveryCountry"] = json!(country);
        assert!(serde_json::from_value::<Request>(request).is_err());
    }
    for city in ["   ".to_owned(), "Toronto\n".to_owned(), "x".repeat(129)] {
        let mut request = rfq(1);
        request["deliveryCity"] = json!(city);
        assert!(
            store
                .apply(serde_json::from_value(request).unwrap(), "invalid-city")
                .is_err()
        );
    }
}
struct TestServer {
    url: String,
    supplier: Arc<Supplier>,
    task: tokio::task::JoinHandle<()>,
}

#[tokio::test]
async fn streaming_unapproved_purchase_expires_without_a_receipt_or_inference() {
    let server = TestServer::with_supplier(
        Supplier::new(Arc::new(NoInference)).with_verifier(Arc::new(ExpiringVerifier)),
    ).await;
    let quoted = server.send(vec![Part::data(rfq(1)).with_media_type(RFQ)], "expired-stream").await;
    let quote = quoted["task"]["artifacts"][0]["parts"][0]["data"]["quoteId"].as_str().unwrap();
    let mut message = Message::new(Role::User, vec![Part::data(order(quote, "expired-po")).with_media_type(PURCHASE)]);
    message.context_id = Some("expired-stream".into());
    let wire = tokio::time::timeout(std::time::Duration::from_secs(10), async {
        reqwest::Client::new().post(format!("{}/a2a", server.url))
            .header("A2A-Version", "1.0")
            .json(&json!({"jsonrpc":"2.0","id":8,"method":"SendStreamingMessage","params":{"message":message}}))
            .send().await.unwrap().text().await.unwrap()
    }).await.unwrap();
    assert!(wire.contains("TASK_STATE_AUTH_REQUIRED"));
    assert!(wire.contains("TASK_STATE_FAILED"));
    assert!(wire.contains("Wallet authorization expired"));
    assert!(!wire.contains("artifactUpdate"));
    assert_eq!(server.supplier.metrics.inference_attempts.load(Ordering::Relaxed), 0);
}

#[tokio::test]
async fn streaming_wallet_approval_emits_receipt_and_completed_without_resending_or_inference() {
    let verifier = Arc::new(TestVerifier::default());
    let server = TestServer::with_supplier(
        Supplier::new(Arc::new(NoInference)).with_verifier(verifier.clone()),
    ).await;
    let quoted = server.send(vec![Part::data(rfq(1)).with_media_type(RFQ)], "stream-context").await;
    let quote = quoted["task"]["artifacts"][0]["parts"][0]["data"]["quoteId"].as_str().unwrap();
    let mut message = Message::new(Role::User, vec![Part::data(order(quote, "stream-po")).with_media_type(PURCHASE)]);
    message.context_id = Some("stream-context".into());
    let mut response = reqwest::Client::new().post(format!("{}/a2a", server.url))
        .header("A2A-Version", "1.0")
        .json(&json!({"jsonrpc":"2.0","id":8,"method":"SendStreamingMessage",
            "params":{"message":message,"configuration":{"acceptedOutputModes":["text/plain",RECEIPT]}}}))
        .send().await.unwrap().error_for_status().unwrap();
    let mut wire = String::new();
    tokio::time::timeout(std::time::Duration::from_secs(10), async {
        loop {
            wire.push_str(&String::from_utf8(response.chunk().await.unwrap().unwrap().to_vec()).unwrap());
            if wire.contains("TASK_STATE_AUTH_REQUIRED") { break; }
        }
        assert!(!wire.contains("order-receipt"));
        verifier.0.store(true, Ordering::SeqCst);
        while let Some(chunk) = response.chunk().await.unwrap() {
            wire.push_str(&String::from_utf8(chunk.to_vec()).unwrap());
        }
    }).await.unwrap();
    let events: Vec<Value> = wire.split("\n\n").filter_map(|frame|
        frame.lines().find_map(|line| line.strip_prefix("data:"))
            .and_then(|data| serde_json::from_str(data.trim()).ok())).collect();
    let status = events.iter().find(|event| event["result"]["statusUpdate"]["status"]["state"] == "TASK_STATE_COMPLETED").unwrap();
    let artifact = events.iter().find(|event| event["result"]["artifactUpdate"].is_object()).unwrap();
    let receipt = &artifact["result"]["artifactUpdate"]["artifact"]["parts"][0]["data"];
    assert_eq!(receipt["purchaseOrderId"], "stream-po");
    assert_eq!(receipt["quoteId"], quote);
    assert_eq!(receipt["ageVerification"], "verified-demo-over-21");
    assert_eq!(status["result"]["statusUpdate"]["status"]["message"]["metadata"]["agentcon.llmCalls"].as_f64(), Some(0.0));
    assert_eq!(server.supplier.metrics.structured_requests.load(Ordering::Relaxed), 2);
    assert_eq!(server.supplier.metrics.inference_attempts.load(Ordering::Relaxed), 0);
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl TestServer {
    async fn new(interpreter: Arc<dyn Interpreter>) -> Self {
        Self::with_supplier(Supplier::new(interpreter)).await
    }

    async fn with_supplier(supplier: Supplier) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
        let supplier = Arc::new(supplier);
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
    let verifier = Arc::new(TestVerifier::default());
    let server = TestServer::with_supplier(
        Supplier::new(Arc::new(NoInference)).with_verifier(verifier.clone()),
    )
    .await;
    let card: Value = reqwest::get(format!("{}/.well-known/agent-card.json", server.url))
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(card["supportedInterfaces"][0]["protocolBinding"], "JSONRPC");
    assert_eq!(card["supportedInterfaces"][0]["protocolVersion"], "1.0");
    assert!(
        !card["defaultInputModes"]
            .as_array()
            .unwrap()
            .contains(&json!("application/json"))
    );
    assert!(
        !card["skills"][0]["inputModes"]
            .as_array()
            .unwrap()
            .contains(&json!("application/json"))
    );
    let discovery = &card["capabilities"]["extensions"]
        .as_array()
        .unwrap()
        .iter()
        .find(|extension| extension["uri"] == "urn:agentcon:extension:structured-schemas:v1")
        .unwrap()["params"]["schemas"];
    assert_eq!(discovery.as_array().unwrap().len(), 4);
    let schema_response = reqwest::get(discovery[0]["url"].as_str().unwrap())
        .await
        .unwrap();
    assert_eq!(
        schema_response.headers()["content-type"],
        "application/schema+json"
    );
    let schema: Value = schema_response.json().await.unwrap();
    assert_eq!(schema["$defs"]["rfq"]["properties"]["kind"]["const"], "rfq");
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
        purchased["task"]["status"]["state"], "TASK_STATE_AUTH_REQUIRED",
        "{purchased}"
    );
    assert!(purchased["task"].get("artifacts").is_none());
    assert_eq!(
        purchased["task"]["status"]["message"]["metadata"][EXTENSION]["authorizationRequest"]["client_id"],
        "redirect_uri:https://verifier.example/response"
    );
    let mut continuation = Message::new(
        Role::User,
        vec![
            Part::data(order("changed-quote-must-not-execute", "changed-po"))
                .with_media_type(PURCHASE),
        ],
    );
    continuation.task_id = Some(purchased["task"]["id"].as_str().unwrap().into());
    continuation.context_id = Some("http-test".into());
    let waiting = server
        .rpc("SendMessage", json!({"message":continuation}))
        .await;
    assert_eq!(
        waiting["task"]["status"]["state"],
        "TASK_STATE_AUTH_REQUIRED"
    );
    assert!(waiting["task"].get("artifacts").is_none());
    verifier.0.store(true, Ordering::SeqCst);
    let purchased = server
        .rpc("SendMessage", json!({"message":continuation}))
        .await;
    assert_eq!(
        purchased["task"]["status"]["state"], "TASK_STATE_COMPLETED",
        "{purchased}"
    );
    assert_eq!(
        purchased["task"]["artifacts"][0]["parts"][0]["mediaType"],
        RECEIPT
    );
    let receipt = &purchased["task"]["artifacts"][0]["parts"][0]["data"];
    assert_eq!(receipt["purchaseOrderId"], "http-po");
    assert_eq!(receipt["quoteId"], quote["quoteId"]);
    assert_eq!(receipt["ageVerification"], "verified-demo-over-21");
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
async fn unconfigured_verifier_never_creates_purchase_and_quotes_remain_open() {
    let server = TestServer::new(Arc::new(NoInference)).await;
    let quote = server
        .send(vec![Part::data(rfq(1)).with_media_type(RFQ)], "no-verifier")
        .await;
    assert_eq!(quote["task"]["status"]["state"], "TASK_STATE_COMPLETED");
    let id = quote["task"]["artifacts"][0]["parts"][0]["data"]["quoteId"]
        .as_str()
        .unwrap();
    let order = server
        .send(
            vec![Part::data(order(id, "po")).with_media_type(PURCHASE)],
            "no-verifier",
        )
        .await;
    assert_eq!(order["task"]["status"]["state"], "TASK_STATE_AUTH_REQUIRED");
    assert!(order["task"].get("artifacts").is_none());
    assert!(
        order["task"]["status"]["message"]["parts"][0]["text"]
            .as_str()
            .unwrap()
            .contains("not configured")
    );
}
#[tokio::test]
async fn invalid_structured_input_never_falls_back_to_inference() {
    let server = TestServer::new(Arc::new(NoInference)).await;
    for parts in [
        vec![Part::data(json!({"wine":"Barolo"}))],
        vec![Part::data(rfq(1)).with_media_type(PURCHASE)],
        vec![Part::data(rfq(1)).with_media_type("application/json")],
        vec![Part::data(rfq(1))],
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
    let health: Value = reqwest::get(format!("{}/health", server.url))
        .await.unwrap().json().await.unwrap();
    assert!(health["llm"]["instanceId"].as_str().is_some_and(|id| !id.is_empty()));
    assert!(health["llm"]["calls"].is_u64());
    assert!(health["llm"]["responseTimeMs"].as_f64().is_some_and(|ms| ms >= 0.0));
    assert!(health["llm"]["timedCalls"].is_u64());
    assert!(health["llm"]["complete"].is_boolean());
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

#[tokio::test]
async fn output_negotiation_and_per_message_zero_inference_counts() {
    let server = TestServer::new(Arc::new(NoInference)).await;
    for (index, modes) in [
        json!(["text/plain"]),
        json!(["text/plain", QUOTE]),
        json!([QUOTE]),
        json!([]),
        json!(["application/json"]),
    ]
    .into_iter()
    .enumerate()
    {
        let mut message = Message::new(Role::User, vec![Part::data(rfq(12)).with_media_type(RFQ)]);
        message.context_id = Some(format!("negotiation-{index}"));
        let value: Value = reqwest::Client::new()
            .post(format!("{}/a2a", server.url))
            .header("A2A-Version", "1.0")
            .json(
                &json!({"jsonrpc":"2.0","id":7,"method":"SendMessage","params":{
                    "message":message, "metadata":null, "configuration":{"acceptedOutputModes":modes}
                }}),
            )
            .send()
            .await
            .unwrap()
            .json()
            .await
            .unwrap();
        if index >= 3 {
            assert_eq!(value["error"]["code"], -32005, "{value}");
            continue;
        }
        let task = &value["result"]["task"];
        assert_eq!(task["status"]["state"], "TASK_STATE_COMPLETED", "{value}");
        assert_eq!(task["metadata"]["agentcon.llmCalls"].as_f64(), Some(0.0));
        if index == 0 {
            assert_eq!(task["artifacts"][0]["name"], "quote");
            assert_eq!(task["artifacts"][0]["parts"][0]["mediaType"], "text/plain");
            assert!(task["artifacts"][0]["parts"][0].get("data").is_none());
            assert!(
                task["status"]["message"]["parts"][0]["text"]
                    .as_str()
                    .unwrap()
                    .contains("1753.00")
            );
        } else {
            assert_eq!(task["artifacts"][0]["parts"][0]["mediaType"], QUOTE);
            if index == 2 {
                assert!(task["status"]["message"].is_null(), "{value}");
            }
        }
        let saved = server.rpc("GetTask", json!({"id":task["id"]})).await;
        assert_eq!(saved["artifacts"], task["artifacts"]);
    }
    assert_eq!(
        server
            .supplier
            .metrics
            .structured_requests
            .load(Ordering::Relaxed),
        3
    );
}

#[async_trait]
impl Interpreter for AuthInterpreter {
    async fn interpret(&self, _: &str, _: Option<&str>, _: &str) -> Result<Intent, String> {
        Ok(Intent::AuthorizationRequired)
    }
}

#[tokio::test]
async fn caller_age_assertion_cannot_authorize_a_purchase_without_quote() {
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
    assert!(text.contains("quote-bound purchase request"));
    assert!(text.contains("No order has been created"));
}
