use std::{path::PathBuf, sync::Arc};

use agentcon_supplier::inference::{Interpreter, ModelProvider, OpenAiInterpreter};
#[cfg(feature = "copilot")]
use agentcon_supplier::inference::CopilotInterpreter;
use agentcon_supplier::{Supplier, app_with_handler, request_handler, slim};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let port: u16 = std::env::var("SUPPLIER_PORT")
        .unwrap_or_else(|_| "6000".into())
        .parse()?;
    // `agentcon-supplier ready-probe`: exit 0 when the running supplier on SUPPLIER_PORT is ready. The HTTP
    // interface listens on loopback only, so orchestrators probe it from inside the container this way.
    if std::env::args().nth(1).as_deref() == Some("ready-probe") {
        let status = reqwest::get(format!("http://127.0.0.1:{port}/ready")).await.map(|response| response.status());
        std::process::exit(if matches!(status, Ok(status) if status.is_success()) { 0 } else { 1 });
    }
    // `agentcon-supplier publish-card <base-url> <slim-url>`: print the Agent Card to publish for a seller reachable
    // only over SLIM, with its schema links under <base-url>. Uses SUPPLIER_INVENTORY and SUPPLIER_STRUCTURED_DATA.
    if std::env::args().nth(1).as_deref() == Some("publish-card") {
        const USAGE: &str = "Usage: agentcon-supplier publish-card <https-origin> <slim-url> [--http] [--oidc <discovery-url>]";
        let args: Vec<String> = std::env::args().skip(2).collect();
        let (Some(origin), Some(slim_url)) = (args.first(), args.get(1)) else { return Err(USAGE.into()) };
        if !origin.starts_with("https://") || !slim_url.starts_with("slim://") {
            return Err("publish-card needs an https:// origin and a slim:// URL.".into());
        }
        let (mut http, mut oidc) = (false, None);
        let mut rest = args[2..].iter();
        while let Some(flag) = rest.next() {
            match flag.as_str() {
                "--http" => http = true,
                "--oidc" => oidc = Some(rest.next().filter(|url| url.starts_with("https://")).ok_or(USAGE)?.clone()),
                _ => return Err(USAGE.into()),
            }
        }
        let structured = std::env::var("SUPPLIER_STRUCTURED_DATA").map_or(Ok(true), |value| value.parse::<bool>())?;
        let card = agentcon_supplier::published_card(origin, structured, load_inventory()?, slim_url, http, oidc.as_deref());
        println!("{}", serde_json::to_string_pretty(&card)?);
        return Ok(());
    }
    let telemetry = agentcon_supplier::telemetry::init()?;
    #[cfg(feature = "copilot")]
    let state_dir = {
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
        // The checkout this binary was built from; absent in a container image, where there is nothing to protect.
        let repository = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .ok_or("Supplier project must have a repository parent")?
            .canonicalize()
            .ok();
        let resolved_state = std::path::absolute(&state_dir)?;
        if repository.is_some_and(|repository| resolved_state.starts_with(repository)) {
            return Err(
                "Copilot state must be outside the repository, not a tracked credential directory."
                    .into(),
            );
        }
        state_dir
    };
    // SUPPLIER_INVENTORY: path to an inventory JSON file; unset uses the built-in 2019 Barolo stock.
    let inventory = load_inventory()?;
    // An OpenAI-compatible endpoint (MODEL_*), as in containers; otherwise the developer's GitHub Copilot.
    #[cfg(feature = "copilot")]
    let mut copilot: Option<Arc<CopilotInterpreter>> = None;
    let interpreter: Arc<dyn Interpreter> = match ModelProvider::from_env()? {
        Some(provider) => {
            tracing::info!(model = provider.model(), "Supplier inference uses an OpenAI-compatible endpoint");
            Arc::new(OpenAiInterpreter::new(provider, inventory)?)
        }
        #[cfg(feature = "copilot")]
        None => {
            let model = std::env::var("SUPPLIER_MODEL").unwrap_or_else(|_| "auto".into());
            let interpreter = Arc::new(CopilotInterpreter::new(state_dir, model, inventory));
            copilot = Some(interpreter.clone());
            interpreter
        }
        #[cfg(not(feature = "copilot"))]
        None => return Err("Set MODEL_API_KEY_FILE, MODEL_BASE_URL_FILE and MODEL_NAME: this build has no GitHub Copilot interpreter.".into()),
    };
    let mut supplier = Supplier::new(interpreter.clone()).with_inventory(inventory);
    if let Ok(value) = std::env::var("SUPPLIER_STRUCTURED_DATA") {
        supplier = supplier.with_structured(value.parse::<bool>()?);
    }
    if let Ok(token) = std::env::var("SUPPLIER_DEMO_CONTROL_TOKEN") {
        supplier = supplier.with_control_token(token)?;
    }
    // The verifier's admin token comes from SUPPLIER_VERIFIER_TOKEN or, in a cluster, a mounted secret file.
    let verifier_token = match std::env::var("SUPPLIER_VERIFIER_TOKEN_FILE") {
        Ok(path) => Ok(std::fs::read_to_string(&path)
            .map_err(|e| format!("Cannot read SUPPLIER_VERIFIER_TOKEN_FILE {path}: {e}"))?
            .trim()
            .to_owned()),
        Err(_) => std::env::var("SUPPLIER_VERIFIER_TOKEN"),
    };
    match (std::env::var("SUPPLIER_VERIFIER_URL"), verifier_token) {
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
        _ => return Err("Set both SUPPLIER_VERIFIER_URL and SUPPLIER_VERIFIER_TOKEN (or SUPPLIER_VERIFIER_TOKEN_FILE).".into()),
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
            if let Err(error) = slim::serve(slim_handler, settings, move |ready| ready_supplier.set_slim_ready(ready)).await {
                tracing::error!(%error, "SLIMRPC binding unavailable; HTTP JSON-RPC continues");
            }
        });
    }
    // Loopback only by default. In a cluster, SUPPLIER_BIND=0.0.0.0 lets the gateway in front of the seller reach it
    // (a NetworkPolicy keeps everything else out), and SUPPLIER_PUBLIC_URL is the origin its card advertises.
    let bind: std::net::IpAddr = std::env::var("SUPPLIER_BIND").as_deref().unwrap_or("127.0.0.1").parse()?;
    let listener = tokio::net::TcpListener::bind((bind, port)).await?;
    let origin = std::env::var("SUPPLIER_PUBLIC_URL")
        .map(|url| url.trim_end_matches('/').to_owned())
        .unwrap_or_else(|_| format!("http://localhost:{port}"));
    tracing::info!(%origin, supplier = %inventory.supplier_name, wines = inventory.wines.len(), pid = std::process::id(), "Wine supplier ready");
    let served = axum::serve(listener, app_with_handler(supplier, &origin, handler))
        .with_graceful_shutdown(async {
            if let Err(error) = tokio::signal::ctrl_c().await {
                tracing::error!(%error, "shutdown signal failed");
            }
        })
        .await;
    #[cfg(feature = "copilot")]
    let cleanup = match &copilot { Some(copilot) => copilot.stop().await, None => Ok(()) };
    #[cfg(not(feature = "copilot"))]
    let cleanup: Result<(), String> = Ok(());
    let flush = tokio::task::spawn_blocking(move || telemetry.shutdown()).await?;
    flush?;
    cleanup?;
    served?;
    Ok(())
}

/// The seller's stock: SUPPLIER_INVENTORY (a JSON file), or the built-in Barolo inventory.
fn load_inventory() -> Result<&'static agentcon_supplier::domain::Inventory, Box<dyn std::error::Error>> {
    Ok(match std::env::var_os("SUPPLIER_INVENTORY") {
        Some(path) => {
            let json = std::fs::read_to_string(&path)
                .map_err(|e| format!("Cannot read SUPPLIER_INVENTORY {}: {e}", PathBuf::from(&path).display()))?;
            Box::leak(Box::new(agentcon_supplier::domain::Inventory::from_json(&json)?))
        }
        None => &agentcon_supplier::domain::BAROLO,
    })
}
