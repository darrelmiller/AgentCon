//! Ephemeral supplier fixture, real A2A SDK, no Copilot process or inference.
use agentcon_supplier::{
    Supplier, app,
    inference::{Intent, Interpreter},
};
use async_trait::async_trait;
use std::sync::Arc;

struct NoInference;
#[async_trait]
impl Interpreter for NoInference {
    async fn interpret(&self, _: &str, _: Option<&str>, _: &str) -> Result<Intent, String> {
        Err("Fixture has no inference".into())
    }
}
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let provider = agentcon_supplier::telemetry::init()?;
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await?;
    let origin = format!("http://127.0.0.1:{}", listener.local_addr()?.port());
    println!("TRACE_FIXTURE_URL={origin}");
    let router = app(Arc::new(Supplier::new(Arc::new(NoInference)).with_structured(true)), &origin);
    axum::serve(listener, router)
        .with_graceful_shutdown(async {
            use tokio::io::AsyncBufReadExt;
            let mut line = String::new();
            let _ = tokio::io::BufReader::new(tokio::io::stdin())
                .read_line(&mut line)
                .await;
        })
        .await?;
    tokio::task::spawn_blocking(move || provider.shutdown()).await??;
    Ok(())
}
