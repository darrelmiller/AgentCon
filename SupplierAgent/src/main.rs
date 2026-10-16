use std::{path::PathBuf, sync::Arc};

use agentcon_supplier::{Supplier, app_with_handler, inference::CopilotInterpreter, request_handler, slim};

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
    // SUPPLIER_INVENTORY: path to an inventory JSON file; unset uses the built-in 2019 Barolo stock.
    let inventory: &'static agentcon_supplier::domain::Inventory = match std::env::var_os("SUPPLIER_INVENTORY") {
        Some(path) => {
            let json = std::fs::read_to_string(&path)
                .map_err(|e| format!("Cannot read SUPPLIER_INVENTORY {}: {e}", PathBuf::from(&path).display()))?;
            Box::leak(Box::new(agentcon_supplier::domain::Inventory::from_json(&json)?))
        }
        None => &agentcon_supplier::domain::BAROLO,
    };
    let interpreter = Arc::new(CopilotInterpreter::new(
        state_dir,
        std::env::var("SUPPLIER_MODEL").unwrap_or_else(|_| "auto".into()),
        inventory,
    ));
    let mut supplier = Supplier::new(interpreter.clone()).with_inventory(inventory);
    if let Ok(value) = std::env::var("SUPPLIER_STRUCTURED_DATA") {
        supplier = supplier.with_structured(value.parse::<bool>()?);
    }
    if let Ok(token) = std::env::var("SUPPLIER_DEMO_CONTROL_TOKEN") {
        supplier = supplier.with_control_token(token)?;
    }
    match (
        std::env::var("SUPPLIER_VERIFIER_URL"),
        std::env::var("SUPPLIER_VERIFIER_TOKEN"),
    ) {
        (Ok(origin), Ok(token)) => {
            supplier = supplier.with_verifier(Arc::new(
                agentcon_supplier::authorization::HttpVerifier::new(origin, token)?,
            ));
        }
        (Err(std::env::VarError::NotPresent), Err(std::env::VarError::NotPresent)) => {
            tracing::warn!(
                "Purchase requests will remain auth-required: presentation verifier not configured"
            );
        }
        _ => return Err("Set both SUPPLIER_VERIFIER_URL and SUPPLIER_VERIFIER_TOKEN.".into()),
    }
    // Optional A2A over SLIMRPC (SUPPLIER_SLIM_ENDPOINT, SUPPLIER_SLIM_NAME, SLIM_SHARED_SECRET).
    let slim_settings = slim::SlimSettings::from_env()?;
    if let Some(settings) = &slim_settings {
        supplier = supplier.with_slim_url(settings.card_url());
    }
    let supplier = Arc::new(supplier);
    let handler = request_handler(supplier.clone());
    if let Some(settings) = slim_settings {
        let slim_handler = handler.clone();
        let ready_supplier = supplier.clone();
        tokio::spawn(async move {
            if let Err(error) = slim::serve(slim_handler, settings, move || ready_supplier.mark_slim_ready()).await {
                tracing::error!(%error, "SLIMRPC binding unavailable; HTTP JSON-RPC continues");
            }
        });
    }
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await?;
    let origin = format!("http://localhost:{port}");
    tracing::info!(%origin, supplier = %inventory.supplier_name, wines = inventory.wines.len(), pid = std::process::id(), "Wine supplier ready");
    let served = axum::serve(listener, app_with_handler(supplier, &origin, handler))
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
