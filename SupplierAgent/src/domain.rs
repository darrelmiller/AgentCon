use std::collections::HashMap;

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

pub const RFQ: &str = "application/vnd.agentcon.demo.wine-rfq.v1+json";
pub const QUOTE: &str = "application/vnd.agentcon.demo.wine-quote.v1+json";
pub const PURCHASE: &str = "application/vnd.agentcon.demo.wine-purchase-order.v1+json";
pub const RECEIPT: &str = "application/vnd.agentcon.demo.wine-order-receipt.v1+json";

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize)]
pub enum DeliveryCountry {
    #[default]
    #[serde(rename = "US")]
    UnitedStates,
    #[serde(rename = "CA")]
    Canada,
}

impl DeliveryCountry {
    pub fn code(self) -> &'static str {
        match self {
            Self::UnitedStates => "US",
            Self::Canada => "CA",
        }
    }
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(tag = "kind", rename_all = "kebab-case", deny_unknown_fields)]
pub enum Request {
    Rfq {
        #[serde(rename = "schemaVersion")]
        schema_version: String,
        #[serde(rename = "demoOnly")]
        demo_only: bool,
        wine: String,
        #[serde(deserialize_with = "deserialize_integer")]
        vintage: u16,
        #[serde(deserialize_with = "deserialize_integer")]
        cases: u32,
        #[serde(rename = "deliveryCity")]
        delivery_city: String,
        #[serde(rename = "deliveryCountry", default)]
        delivery_country: DeliveryCountry,
    },
    PurchaseOrder {
        #[serde(rename = "schemaVersion")]
        schema_version: String,
        #[serde(rename = "demoOnly")]
        demo_only: bool,
        #[serde(rename = "quoteId")]
        quote_id: String,
        #[serde(rename = "purchaseOrderId")]
        purchase_order_id: String,
    },
}

impl Request {
    pub fn media_type(&self) -> &'static str {
        match self {
            Self::Rfq { .. } => RFQ,
            Self::PurchaseOrder { .. } => PURCHASE,
        }
    }
}

// A2A's protobuf Struct represents every data-part number as a double.
fn deserialize_integer<'de, D, T>(deserializer: D) -> Result<T, D::Error>
where
    D: serde::Deserializer<'de>,
    T: TryFrom<u64>,
{
    let value = Value::deserialize(deserializer)?;
    let integer = value
        .as_u64()
        .or_else(|| {
            value
                .as_f64()
                .filter(|number| {
                    number.is_finite()
                        && *number >= 0.0
                        && *number <= f64::from(u32::MAX)
                        && number.fract() == 0.0
                })
                .map(|number| number as u64)
        })
        .ok_or_else(|| serde::de::Error::custom("expected a non-negative, bounded whole number"))?;
    T::try_from(integer).map_err(|_| serde::de::Error::custom("integer out of range"))
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WineQuote {
    pub kind: &'static str,
    pub schema_version: &'static str,
    pub demo_only: bool,
    pub quote_id: String,
    pub wine: String,
    pub vintage: u16,
    pub cases: u32,
    pub bottles_per_case: u32,
    pub delivery_city: String,
    pub delivery_country: DeliveryCountry,
    pub currency: &'static str,
    pub case_price_minor: u32,
    pub delivery_minor: u32,
    pub total_minor: u32,
    pub expires_at: DateTime<Utc>,
}

#[derive(Clone)]
struct BoundQuote {
    context: String,
    quote: WineQuote,
}

#[derive(Default)]
pub struct WineStore {
    quotes: HashMap<String, BoundQuote>,
    latest: HashMap<String, String>,
    orders: HashMap<(String, String), (String, Value)>,
}

impl WineStore {
    pub fn latest_quote(&self, context: &str) -> Option<&str> {
        self.latest.get(context).map(String::as_str)
    }

    pub fn validate_purchase(&self, request: &Request, context: &str) -> Result<(), String> {
        let Request::PurchaseOrder {
            schema_version,
            demo_only,
            quote_id,
            purchase_order_id,
        } = request
        else {
            return Err("Expected a purchase order.".into());
        };
        validate_demo(schema_version, *demo_only)?;
        if purchase_order_id.is_empty() || purchase_order_id.len() > 128 {
            return Err("purchaseOrderId must contain 1-128 characters.".into());
        }
        if let Some((prior_quote, _)) = self
            .orders
            .get(&(context.into(), purchase_order_id.clone()))
        {
            return if prior_quote == quote_id {
                Ok(())
            } else {
                Err("purchaseOrderId already used for a different quote; no order changed.".into())
            };
        }
        let bound = self
            .quotes
            .get(quote_id)
            .ok_or("Unknown or expired quoteId; request a new quote before ordering.")?;
        if bound.context != context {
            return Err("Quote belongs to another A2A context; no order created.".into());
        }
        if bound.quote.expires_at <= Utc::now() {
            return Err("Quote expired; no order created.".into());
        }
        if self.orders.len() >= 128 {
            return Err("Demo order capacity reached; restart to clear in-memory records.".into());
        }
        Ok(())
    }

    pub fn apply(
        &mut self,
        request: Request,
        context: &str,
    ) -> Result<(&'static str, Value, String), String> {
        if matches!(&request, Request::PurchaseOrder { .. }) {
            self.validate_purchase(&request, context)?;
        }
        match request {
            Request::Rfq {
                schema_version,
                demo_only,
                wine,
                vintage,
                cases,
                delivery_city,
                delivery_country,
            } => {
                validate_demo(&schema_version, demo_only)?;
                if !wine.eq_ignore_ascii_case("Barolo") || vintage != 2019 {
                    return Err(
                        "Test inventory contains only 2019 Barolo; no substitute was quoted."
                            .into(),
                    );
                }
                if !(1..=48).contains(&cases) {
                    return Err("Demo quote quantity must be 1 through 48 cases.".into());
                }
                if delivery_city.trim().is_empty()
                    || delivery_city.chars().count() > 128
                    || delivery_city.chars().any(char::is_control)
                {
                    return Err("Demo delivery city must contain 1-128 characters without control characters.".into());
                }
                let delivery_city = delivery_city.trim();
                self.quotes
                    .retain(|_, bound| bound.quote.expires_at > Utc::now());
                self.latest.retain(|_, id| self.quotes.contains_key(id));
                if self.quotes.len() >= 128 {
                    return Err("Demo quote capacity reached; wait for expiry or restart.".into());
                }
                let quote = WineQuote {
                    kind: "quote",
                    schema_version: "1",
                    demo_only: true,
                    quote_id: format!("demo-quote-{}", uuid::Uuid::new_v4()),
                    wine: "Barolo".into(),
                    vintage: 2019,
                    cases,
                    bottles_per_case: 12,
                    delivery_city: delivery_city.into(),
                    delivery_country,
                    currency: "USD",
                    case_price_minor: 14_400,
                    delivery_minor: 2_500,
                    total_minor: cases * 14_400 + 2_500,
                    expires_at: Utc::now() + Duration::minutes(15),
                };
                let text = format!(
                    "Quote {}: {} cases of 2019 Barolo (12 bottles/case) to {}, {}. \
                     USD 144.00/case + USD 25.00 demo delivery = USD {:.2}. \
                     Valid 15 minutes.",
                    quote.quote_id,
                    cases,
                    quote.delivery_city,
                    quote.delivery_country.code(),
                    f64::from(quote.total_minor) / 100.0
                );
                let value = serde_json::to_value(&quote).map_err(|e| e.to_string())?;
                self.latest.insert(context.into(), quote.quote_id.clone());
                self.quotes.insert(
                    quote.quote_id.clone(),
                    BoundQuote {
                        context: context.into(),
                        quote,
                    },
                );
                Ok((QUOTE, value, text))
            }
            Request::PurchaseOrder {
                quote_id,
                purchase_order_id,
                ..
            } => {
                let key = (context.to_string(), purchase_order_id.clone());
                if let Some((_, receipt)) = self.orders.get(&key) {
                    return Ok((RECEIPT, receipt.clone(), receipt_text(&purchase_order_id)));
                }
                let bound = self
                    .quotes
                    .get(&quote_id)
                    .ok_or("Unknown or expired quoteId; request a new quote before ordering.")?;
                let receipt = json!({
                    "kind": "order-receipt", "schemaVersion": "1", "demoOnly": true,
                    "orderId": format!("demo-order-{}", uuid::Uuid::new_v4()),
                    "purchaseOrderId": purchase_order_id, "quoteId": quote_id,
                    "status": "simulated", "currency": "USD", "totalMinor": bound.quote.total_minor,
                    "ageVerification": "not-performed",
                    "payment": "not-performed", "fulfillment": "not-performed"
                });
                self.orders.insert(key, (quote_id, receipt.clone()));
                Ok((RECEIPT, receipt, receipt_text(&purchase_order_id)))
            }
        }
    }
}

fn validate_demo(version: &str, demo_only: bool) -> Result<(), String> {
    if version != "1" {
        return Err("Only schemaVersion \"1\" is supported.".into());
    }
    if !demo_only {
        return Err("Only demoOnly:true is allowed. Real purchases are not implemented.".into());
    }
    Ok(())
}

fn receipt_text(id: &str) -> String {
    format!("Purchase order {id} recorded.")
}
