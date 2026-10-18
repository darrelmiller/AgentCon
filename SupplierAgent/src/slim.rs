//! A2A over SLIMRPC: the supplier also serves its A2A request handler as a SLIM app, so callers
//! reach it by SLIM name through a SLIM node instead of by URL. The supplier only dials out to
//! the node; it needs no inbound port for this binding.

use std::sync::Arc;

use a2a::*;
use a2a_server::{RequestHandler, ServiceParams};
use a2a_slimrpc::SlimRpcHandler;
use async_trait::async_trait;
use futures::stream::BoxStream;
use slim_auth::auth_provider::{AuthProvider, AuthVerifier};
use slim_auth::shared_secret::SharedSecret;
use slim_config::auth::identity::{IdentityProviderConfig, IdentityVerifierConfig};
use slim_config::client::ClientConfig;
use slim_datapath::api::ProtoName;
use slim_rpc::Server;
use slim_service::service::{Service, ServiceBuilder};

/// SLIM settings: the node connection (SUPPLIER_SLIM_CONFIG or SUPPLIER_SLIM_ENDPOINT), the app
/// name (SUPPLIER_SLIM_NAME) and the app identity secret (SLIM_SHARED_SECRET).
#[derive(Clone, Debug)]
pub struct SlimSettings {
    /// How to reach the SLIM node (endpoint, TLS, gateway auth such as an OIDC token).
    pub client: ClientConfig,
    /// SLIM app name, org/namespace/app, e.g. agntcy/demo/barolo-supplier.
    pub name: String,
    /// The app's SLIM identity: its own credential (provider) and whom it trusts (verifier).
    pub identity: Identity,
}

/// How a SLIM app proves who it is to its peers (the MLS credential) and which peers it accepts.
#[derive(Clone, Debug)]
pub enum Identity {
    /// Every demo app knows one secret; anyone holding it can claim any name.
    SharedSecret(String),
    /// From the config file's `app` section, e.g. a per-agent Ed25519 JWT (did:key) signer and a
    /// JWKS of trusted agents as verifier.
    Configured { provider: IdentityProviderConfig, verifier: IdentityVerifierConfig },
}

impl SlimSettings {
    /// None when neither SUPPLIER_SLIM_CONFIG nor SUPPLIER_SLIM_ENDPOINT is set (SLIMRPC disabled).
    ///
    /// SUPPLIER_SLIM_CONFIG names a YAML file whose `client:` section is a slim_config ClientConfig,
    /// the same file the a2a CLI SLIMRPC plugin reads, e.g. for the OIDC gateway:
    ///   client:
    ///     endpoint: "https://oidc.slim.outshift.io:443"
    ///     tls: { insecure: false, include_system_ca_certs_pool: true }
    ///     auth: { type: static_jwt, file: /Users/me/.slimctl/token, duration: 5m }
    /// An optional `app:` section with `identity_provider` / `identity_verifier` (the CLI plugin's
    /// schema) sets the app identity; without it, SLIM_SHARED_SECRET is used.
    /// SUPPLIER_SLIM_ENDPOINT alone (e.g. http://localhost:46357) is a plaintext local node.
    pub fn from_env() -> Result<Option<Self>, String> {
        #[derive(serde::Deserialize)]
        struct App {
            identity_provider: IdentityProviderConfig,
            identity_verifier: IdentityVerifierConfig,
        }
        let (client, app) = match (std::env::var_os("SUPPLIER_SLIM_CONFIG"), std::env::var("SUPPLIER_SLIM_ENDPOINT")) {
            (Some(path), _) => {
                #[derive(serde::Deserialize)]
                struct File {
                    client: ClientConfig,
                    #[serde(default)]
                    app: Option<App>,
                }
                let bytes = std::fs::read(&path)
                    .map_err(|e| format!("Cannot read SUPPLIER_SLIM_CONFIG {}: {e}", std::path::Path::new(&path).display()))?;
                let file = serde_yaml::from_slice::<File>(&bytes)
                    .map_err(|e| format!("Invalid SUPPLIER_SLIM_CONFIG: {e}"))?;
                (file.client, file.app)
            }
            (None, Ok(endpoint)) => {
                let mut client = ClientConfig::with_endpoint(&endpoint);
                // A local slimctl node is plaintext; https:// endpoints keep TLS verification.
                client.tls_setting.insecure = endpoint.starts_with("http://");
                (client, None)
            }
            (None, Err(_)) => return Ok(None),
        };
        let name = std::env::var("SUPPLIER_SLIM_NAME")
            .map_err(|_| "Set SUPPLIER_SLIM_NAME (org/namespace/app) to serve SLIMRPC.")?;
        let identity = match app {
            Some(app) => Identity::Configured { provider: app.identity_provider, verifier: app.identity_verifier },
            None => {
                let secret = std::env::var("SLIM_SHARED_SECRET")
                    .map_err(|_| "Set SLIM_SHARED_SECRET, or an app identity in SUPPLIER_SLIM_CONFIG, to serve SLIMRPC.")?;
                if secret.len() < 32 {
                    return Err("SLIM_SHARED_SECRET must contain at least 32 characters.".into());
                }
                Identity::SharedSecret(secret)
            }
        };
        let settings = Self { client, name, identity };
        settings.proto_name()?;
        Ok(Some(settings))
    }

    fn proto_name(&self) -> Result<ProtoName, String> {
        match self.name.split('/').collect::<Vec<_>>().as_slice() {
            [org, namespace, app] if [org, namespace, app].iter().all(|part| {
                !part.is_empty() && part.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
            }) => Ok(ProtoName::from_strings([*org, *namespace, *app])),
            _ => Err(format!("SUPPLIER_SLIM_NAME {:?} must be org/namespace/app.", self.name)),
        }
    }

    /// The Agent Card interface URL for this binding.
    pub fn card_url(&self) -> String {
        format!("slim://{}", self.name)
    }
}

/// SLIMRPC bypasses the HTTP JSON-RPC middleware, so apply the same executor bridge for output
/// negotiation and streaming authorization completion before dispatch.
struct NegotiatingHandler<H>(Arc<H>);

fn negotiated(mut req: SendMessageRequest, streaming: bool) -> Result<SendMessageRequest, A2AError> {
    let accepted = req.configuration.as_ref().and_then(|config| config.accepted_output_modes.clone());
    if accepted.as_ref().is_some_and(|modes| {
        modes.len() > 16 || modes.iter().any(|mode| mode.is_empty() || mode.len() > 256)
    }) {
        return Err(A2AError::invalid_params("Invalid acceptedOutputModes."));
    }
    let metadata = req.metadata.get_or_insert_with(Default::default);
    metadata.insert("agentcon.internal.watchAuthorization".into(), serde_json::json!(streaming));
    metadata.remove("agentcon.internal.acceptedOutputModes");
    if let Some(accepted) = accepted {
        metadata.insert("agentcon.internal.acceptedOutputModes".into(), serde_json::json!(accepted));
    }
    Ok(req)
}

#[async_trait]
impl<H: RequestHandler> RequestHandler for NegotiatingHandler<H> {
    async fn send_message(&self, params: &ServiceParams, req: SendMessageRequest)
        -> Result<SendMessageResponse, A2AError> {
        self.0.send_message(params, negotiated(req, false)?).await
    }

    async fn send_streaming_message(&self, params: &ServiceParams, req: SendMessageRequest)
        -> Result<BoxStream<'static, Result<StreamResponse, A2AError>>, A2AError> {
        self.0.send_streaming_message(params, negotiated(req, true)?).await
    }

    async fn get_task(&self, params: &ServiceParams, req: GetTaskRequest) -> Result<Task, A2AError> {
        self.0.get_task(params, req).await
    }

    async fn list_tasks(&self, params: &ServiceParams, req: ListTasksRequest)
        -> Result<ListTasksResponse, A2AError> {
        self.0.list_tasks(params, req).await
    }

    async fn cancel_task(&self, params: &ServiceParams, req: CancelTaskRequest) -> Result<Task, A2AError> {
        self.0.cancel_task(params, req).await
    }

    async fn subscribe_to_task(&self, params: &ServiceParams, req: SubscribeToTaskRequest)
        -> Result<BoxStream<'static, Result<StreamResponse, A2AError>>, A2AError> {
        self.0.subscribe_to_task(params, req).await
    }

    async fn create_push_config(&self, params: &ServiceParams, req: TaskPushNotificationConfig)
        -> Result<TaskPushNotificationConfig, A2AError> {
        self.0.create_push_config(params, req).await
    }

    async fn get_push_config(&self, params: &ServiceParams, req: GetTaskPushNotificationConfigRequest)
        -> Result<TaskPushNotificationConfig, A2AError> {
        self.0.get_push_config(params, req).await
    }

    async fn list_push_configs(&self, params: &ServiceParams, req: ListTaskPushNotificationConfigsRequest)
        -> Result<ListTaskPushNotificationConfigsResponse, A2AError> {
        self.0.list_push_configs(params, req).await
    }

    async fn delete_push_config(&self, params: &ServiceParams, req: DeleteTaskPushNotificationConfigRequest)
        -> Result<(), A2AError> {
        self.0.delete_push_config(params, req).await
    }

    async fn get_extended_agent_card(&self, params: &ServiceParams, req: GetExtendedAgentCardRequest)
        -> Result<AgentCard, A2AError> {
        self.0.get_extended_agent_card(params, req).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(modes: Option<Vec<String>>) -> SendMessageRequest {
        SendMessageRequest {
            message: Message::new(Role::User, vec![Part::text("buy")]),
            configuration: modes.map(|modes| SendMessageConfiguration {
                accepted_output_modes: Some(modes), task_push_notification_config: None,
                history_length: None, return_immediately: None,
            }),
            metadata: Some(std::collections::HashMap::from([
                ("agentcon.internal.acceptedOutputModes".into(), serde_json::json!(["spoofed"])),
                ("agentcon.internal.watchAuthorization".into(), serde_json::json!(true)),
            ])),
            tenant: None,
        }
    }

    #[test]
    fn slim_streaming_requests_get_the_same_authorization_bridge_as_http() {
        let streamed = negotiated(request(Some(vec!["text/plain".into()])), true).unwrap().metadata.unwrap();
        assert_eq!(streamed["agentcon.internal.watchAuthorization"], true);
        assert_eq!(streamed["agentcon.internal.acceptedOutputModes"], serde_json::json!(["text/plain"]));
        let sent = negotiated(request(None), false).unwrap().metadata.unwrap();
        assert_eq!(sent["agentcon.internal.watchAuthorization"], false);
        assert!(!sent.contains_key("agentcon.internal.acceptedOutputModes"));
        assert!(negotiated(request(Some(vec![String::new()])), true).is_err());
    }
}

/// How often the node connection is checked for `on_status`.
const CONNECTION_CHECK: std::time::Duration = std::time::Duration::from_secs(5);

/// Connects to the SLIM node, registers the app name, and serves `handler` until the process exits.
/// `on_status(true)` is called once registered; afterwards `on_status` follows the node connection, which
/// drops out of the service once its client stops reconnecting.
pub async fn serve<H: RequestHandler + 'static>(
    handler: Arc<H>,
    settings: SlimSettings,
    on_status: impl Fn(bool) + Send + Sync + 'static,
) -> Result<(), String> {
    let name = settings.proto_name()?;
    let (provider, verifier) = match &settings.identity {
        Identity::SharedSecret(secret) => {
            let secret = SharedSecret::new(&settings.name, secret)
                .map_err(|e| format!("Invalid SLIM shared secret: {e}"))?;
            (AuthProvider::shared_secret(secret.clone()), AuthVerifier::shared_secret(secret))
        }
        Identity::Configured { provider, verifier } => (
            provider.build_auth_provider().map_err(|e| format!("Invalid SLIM identity provider: {e}"))?,
            verifier.build_auth_verifier().map_err(|e| format!("Invalid SLIM identity verifier: {e}"))?,
        ),
    };
    let component = settings.name.replace(['/', '_'], "-");
    let id = slim_config::component::id::ID::new_with_name(ServiceBuilder::kind(), &component)
        .map_err(|e| format!("Invalid SLIM service id: {e}"))?;
    let service = Arc::new(Service::new(id));
    let endpoint = settings.client.endpoint.clone();
    let connection = service
        .connect(&settings.client)
        .await
        .map_err(|e| format!("SLIM node {endpoint} unreachable: {e:?}"))?;
    let (app, notifications) = service
        .create_app(&name, provider, verifier)
        .map_err(|e| format!("SLIM create_app failed: {e}"))?;
    let app = Arc::new(app);
    // create_app subscribes locally; propagate the name to the node so callers can route to us.
    app.subscribe(&name, Some(connection))
        .await
        .map_err(|e| format!("SLIM subscribe failed: {e}"))?;
    let server = Server::new(app.clone(), app.app_name().clone(), notifications);
    SlimRpcHandler::new(Arc::new(NegotiatingHandler(handler))).register(&server);
    let identity = match &settings.identity { Identity::SharedSecret(_) => "shared-secret", Identity::Configured { .. } => "configured" };
    tracing::info!(name = %settings.name, %endpoint, identity, "SLIMRPC server registered");
    on_status(true);
    let monitor = service.clone();
    let watch = tokio::spawn(async move {
        let mut connected = true;
        let mut tick = tokio::time::interval(CONNECTION_CHECK);
        loop {
            tick.tick().await;
            let now = monitor.get_all_connections().iter().any(|info| info.id == connection);
            if now != connected {
                if now { tracing::info!(%endpoint, "SLIM node connection restored") } else { tracing::warn!(%endpoint, "SLIM node connection lost") }
                connected = now;
                on_status(now);
            }
        }
    });
    let result = server.serve().await.map_err(|e| format!("SLIMRPC server stopped: {e}"));
    watch.abort();
    result
}
