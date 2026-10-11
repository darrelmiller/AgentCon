use std::{path::PathBuf, sync::Arc};

use agentcon_supplier::{Supplier, app, inference::CopilotInterpreter};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let telemetry = agentcon_supplier::telemetry::init()?;
    let port: u16 = std::env::var("SUPPLIER_PORT")
        .unwrap_or_else(|_| "6000".into())
        .parse()?;
    let state_dir = match std::env::var_os("SUPPLIER_COPILOT_STATE") {
        Some(path) => PathBuf::from(path),
        None => PathBuf::from(
            std::env::var_os("LOCALAPPDATA").ok_or("Set SUPPLIER_COPILOT_STATE or LOCALAPPDATA")?,
        )
        .join("AgentConDemo")
        .join("supplier-copilot"),
    };
    if !state_dir.is_absolute() {
        return Err("SUPPLIER_COPILOT_STATE must be absolute and outside the repository.".into());
    }
    let repository = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .ok_or("Supplier project must have a repository parent")?
        .canonicalize()?;
    let resolved_state = std::path::absolute(&state_dir)?;
    if resolved_state.starts_with(&repository) {
        return Err(
            "Copilot state must be outside the repository, not a tracked credential directory."
                .into(),
        );
    }
    let interpreter = Arc::new(CopilotInterpreter::new(
        state_dir,
        std::env::var("SUPPLIER_MODEL").unwrap_or_else(|_| "auto".into()),
    ));
    let supplier = Arc::new(Supplier::new(interpreter.clone()));
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await?;
    let origin = format!("http://localhost:{port}");
    tracing::info!(%origin, pid = std::process::id(), "TEST-ONLY supplier ready; no wallet verification or real purchases");
    let served = axum::serve(listener, app(supplier, &origin))
        .with_graceful_shutdown(async {
            if let Err(error) = tokio::signal::ctrl_c().await {
                tracing::error!(%error, "shutdown signal failed");
            }
        })
        .await;
    let cleanup = interpreter.stop().await;
    let flush = tokio::task::spawn_blocking(move || telemetry.shutdown()).await?;
    flush?;
    cleanup?;
    served?;
    Ok(())
}
