//! A2A request metadata bridge shared by bindings that bypass the HTTP JSON-RPC middleware.

use std::sync::Arc;

use a2a::*;
use a2a_server::{RequestHandler, ServiceParams};
use async_trait::async_trait;
use futures::stream::BoxStream;

/// HTTP+JSON and SLIMRPC bypass the HTTP JSON-RPC middleware, so apply the same executor bridge for output
/// negotiation and streaming authorization completion before dispatch.
pub(crate) struct NegotiatingHandler<H>(pub(crate) Arc<H>);

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
    fn streaming_requests_get_the_same_authorization_bridge_as_http() {
        let streamed = negotiated(request(Some(vec!["text/plain".into()])), true).unwrap().metadata.unwrap();
        assert_eq!(streamed["agentcon.internal.watchAuthorization"], true);
        assert_eq!(streamed["agentcon.internal.acceptedOutputModes"], serde_json::json!(["text/plain"]));
        let sent = negotiated(request(None), false).unwrap().metadata.unwrap();
        assert_eq!(sent["agentcon.internal.watchAuthorization"], false);
        assert!(!sent.contains_key("agentcon.internal.acceptedOutputModes"));
        assert!(negotiated(request(Some(vec![String::new()])), true).is_err());
    }
}
