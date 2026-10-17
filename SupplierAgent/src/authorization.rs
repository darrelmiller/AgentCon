use async_trait::async_trait;
use serde::{Deserialize, Serialize};

pub const EXTENSION: &str =
    "https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/v1";

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Challenge {
    pub id: String,
    pub task_id: String,
    pub context_id: String,
    pub expires_at: i64,
    pub authorization_request: serde_json::Value,
    pub presentation_url: String,
    pub wallet_uri: String,
}

#[async_trait]
pub trait Verifier: Send + Sync {
    async fn create(&self, task: &str, context: &str) -> Result<Challenge, String>;
    async fn verified(&self, challenge: &Challenge) -> Result<bool, String>;
}

pub struct HttpVerifier {
    client: reqwest::Client,
    origin: String,
    token: String,
}

impl HttpVerifier {
    fn supported_client_id(client: &str) -> bool {
        (client.starts_with("decentralized_identifier:did:jwk:") || client.starts_with("did:jwk:"))
            && client.len() <= 2048
            && !client.contains('#')
    }

    pub fn new(origin: String, token: String) -> Result<Self, String> {
        let url = reqwest::Url::parse(&origin).map_err(|_| "Invalid verifier origin")?;
        let loopback = matches!(url.host_str(), Some("localhost" | "127.0.0.1"));
        if !(url.scheme() == "https" || url.scheme() == "http" && loopback)
            || !url.username().is_empty()
            || url.password().is_some()
            || url.path() != "/"
            || url.query().is_some()
            || url.fragment().is_some()
            || token.len() < 32
            || token.len() > 256
            || !token
                .bytes()
                .all(|c| c.is_ascii_alphanumeric() || c == b'_' || c == b'-')
        {
            return Err("Verifier requires an HTTPS origin (HTTP only on loopback) and a 32-256 character base64url admin token.".into());
        }
        let client = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(std::time::Duration::from_secs(10))
            .build()
            .map_err(|_| "Could not initialize verifier client")?;
        Ok(Self {
            client,
            origin: origin.trim_end_matches('/').into(),
            token,
        })
    }
}

#[async_trait]
impl Verifier for HttpVerifier {
    async fn create(&self, task: &str, context: &str) -> Result<Challenge, String> {
        let response = self
            .client
            .post(format!("{}/admin/presentations", self.origin))
            .bearer_auth(&self.token)
            .json(&serde_json::json!({"taskId": task, "contextId": context}))
            .send()
            .await
            .map_err(|_| "Verifier connection failed; no order created")?;
        if !response.status().is_success() {
            return Err(format!(
                "Verifier returned HTTP {}; no order created",
                response.status()
            ));
        }
        let challenge: Challenge = response
            .json()
            .await
            .map_err(|_| "Invalid verifier challenge")?;
        if challenge.task_id != task
            || challenge.context_id != context
            || challenge.expires_at <= chrono::Utc::now().timestamp()
            || challenge.id.len() != 43
            || !challenge
                .id
                .bytes()
                .all(|c| c.is_ascii_alphanumeric() || c == b'_' || c == b'-')
            || challenge.presentation_url
                != format!("{}/presentations/{}", self.origin, challenge.id)
            || challenge.authorization_request["request_uri"]
                != format!("{}/presentations/{}/request", self.origin, challenge.id)
            || !challenge.authorization_request["client_id"]
                .as_str()
                .is_some_and(Self::supported_client_id)
            || !challenge.wallet_uri.starts_with("openid4vp://?")
        {
            return Err("Verifier returned an invalid or unbound challenge".into());
        }

        Ok(challenge)
    }

    async fn verified(&self, challenge: &Challenge) -> Result<bool, String> {
        let response = self
            .client
            .get(format!(
                "{}/admin/presentations/{}",
                self.origin, challenge.id
            ))
            .bearer_auth(&self.token)
            .send()
            .await
            .map_err(|_| "Verifier status connection failed")?;
        if !response.status().is_success() {
            return Err(format!(
                "Verifier status returned HTTP {}; no order created",
                response.status()
            ));
        }
        let status: serde_json::Value = response
            .json()
            .await
            .map_err(|_| "Invalid verifier status")?;
        if status["taskId"] != challenge.task_id
            || status["contextId"] != challenge.context_id
            || status["expiresAt"] != challenge.expires_at
        {
            return Err("Verifier status is not bound to this purchase task".into());
        }
        status["verified"]
            .as_bool()
            .ok_or_else(|| "Invalid verifier status".into())
    }
}

#[cfg(test)]
mod tests {
    use super::{Challenge, HttpVerifier, Verifier};

    #[test]
    fn recognizes_final_and_legacy_did_client_ids() {
        assert!(HttpVerifier::supported_client_id(
            "decentralized_identifier:did:jwk:public-key"
        ));
        assert!(HttpVerifier::supported_client_id("did:jwk:public-key"));
        for client in [
            "https://untrusted.example",
            "decentralized_identifier:did:web:untrusted.example",
            "decentralized_identifier:did:jwk:public-key#0",
        ] {
            assert!(!HttpVerifier::supported_client_id(client));
        }
        assert!(!HttpVerifier::supported_client_id(&format!(
            "did:jwk:{}",
            "x".repeat(2048)
        )));
    }

    #[tokio::test]
    async fn accepts_task_bound_final_verifier_challenge_over_http() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let origin = format!("http://{}", listener.local_addr().unwrap());
        let id = "a".repeat(43);
        let challenge = Challenge {
            id: id.clone(),
            task_id: "purchase".into(),
            context_id: "context".into(),
            expires_at: chrono::Utc::now().timestamp() + 600,
            authorization_request: serde_json::json!({
                "client_id": "decentralized_identifier:did:jwk:public-key",
                "request_uri": format!("{origin}/presentations/{id}/request")
            }),
            presentation_url: format!("{origin}/presentations/{id}"),
            wallet_uri: "openid4vp://?client_id=decentralized_identifier%3Adid%3Ajwk%3Apublic-key"
                .into(),
        };
        let router = axum::Router::new().route(
            "/admin/presentations",
            axum::routing::post(move || {
                let challenge = challenge.clone();
                async move { axum::Json(challenge) }
            }),
        );
        let server = tokio::spawn(async move {
            axum::serve(listener, router).await.unwrap();
        });
        let verifier = HttpVerifier::new(origin, "t".repeat(43)).unwrap();
        let result = verifier.create("purchase", "context").await;
        server.abort();
        assert_eq!(result.unwrap().task_id, "purchase");
    }
}
