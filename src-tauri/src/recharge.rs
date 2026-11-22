use crate::{balance::site_root, store::Profile};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::time::Duration;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PaymentMethod {
    pub name: String,
    #[serde(alias = "type")]
    pub kind: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TopUpInfo {
    pub enabled: bool,
    #[serde(alias = "min_amount")]
    pub min_amount: i64,
    #[serde(alias = "amount_options")]
    pub amount_options: Vec<i64>,
    #[serde(alias = "payment_methods")]
    pub payment_methods: Vec<PaymentMethod>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Checkout {
    #[serde(default, alias = "trade_no")]
    pub trade_no: String,
    #[serde(default)]
    pub amount: i64,
    #[serde(default)]
    pub money: f64,
    #[serde(default, alias = "payment_method")]
    pub payment_method: String,
    #[serde(alias = "pay_url")]
    pub pay_url: Option<String>,
    #[serde(alias = "mobile_pay_url")]
    pub mobile_pay_url: Option<String>,
    #[serde(alias = "qr_code")]
    pub qr_code: Option<String>,
    #[serde(alias = "url_scheme")]
    pub url_scheme: Option<String>,
}

fn credentials(profile: &Profile) -> Result<(url::Url, String), String> {
    let base = site_root(&profile.base_url)?;
    let key = profile.api_key.trim();
    if key.is_empty() || key.chars().any(char::is_control) {
        return Err("请填写有效的 API Key。".into());
    }
    Ok((base, key.to_owned()))
}

fn client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(20))
        .build()
        .map_err(|_| "无法创建充值请求。".to_string())
}

async fn response_data(response: reqwest::Response) -> Result<Value, String> {
    if !response.status().is_success() {
        return Err(format!(
            "充值接口返回 HTTP {}。",
            response.status().as_u16()
        ));
    }
    let bytes = response
        .bytes()
        .await
        .map_err(|_| "读取充值接口响应失败。".to_string())?;
    if bytes.len() > 1024 * 1024 {
        return Err("充值接口响应超过 1 MB。".into());
    }
    let value: Value = serde_json::from_slice(&bytes).map_err(|_| "充值接口未返回有效 JSON。")?;
    if value.get("success").and_then(Value::as_bool) == Some(false) {
        return Err(value
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("充值请求失败。")
            .to_owned());
    }
    Ok(value.get("data").cloned().unwrap_or(value))
}

pub async fn topup_info(profile: &Profile) -> Result<TopUpInfo, String> {
    let (base, key) = credentials(profile)?;
    let url = base
        .join("api/usage/topup/info")
        .map_err(|_| "充值查询地址无效。".to_string())?;
    let value = response_data(
        client()?
            .get(url)
            .bearer_auth(key)
            .header("Accept", "application/json")
            .send()
            .await
            .map_err(|_| "无法连接充值接口。".to_string())?,
    )
    .await?;
    serde_json::from_value(value).map_err(|_| "充值接口返回的数据格式不受支持。".into())
}

pub async fn create_topup(
    profile: &Profile,
    amount: i64,
    payment_method: &str,
) -> Result<Checkout, String> {
    if amount <= 0 || payment_method.trim().is_empty() {
        return Err("请填写有效的充值金额和支付方式。".into());
    }
    let (base, key) = credentials(profile)?;
    let url = base
        .join("api/usage/topup")
        .map_err(|_| "充值地址无效。".to_string())?;
    let value = response_data(
        client()?
            .post(url)
            .bearer_auth(key)
            .header("Accept", "application/json")
            .json(&serde_json::json!({ "amount": amount, "payment_method": payment_method }))
            .send()
            .await
            .map_err(|_| "无法连接充值接口。".to_string())?,
    )
    .await?;
    let checkout = value
        .get("checkout")
        .cloned()
        .ok_or_else(|| "充值接口没有返回付款信息。".to_string())?;
    let order = value.get("order").cloned().unwrap_or_default();
    let mut result: Checkout = serde_json::from_value(checkout)
        .map_err(|_| "充值接口返回的付款信息格式无效。".to_string())?;
    if result.trade_no.is_empty() {
        result.trade_no = order
            .get("trade_no")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_owned();
    }
    if result.amount <= 0 {
        result.amount = order
            .get("amount")
            .and_then(Value::as_i64)
            .unwrap_or(amount);
    }
    if result.payment_method.is_empty() {
        result.payment_method = order
            .get("payment_method")
            .and_then(Value::as_str)
            .unwrap_or(payment_method)
            .to_owned();
    }
    if result.money <= 0.0 {
        result.money = order
            .get("money")
            .and_then(Value::as_f64)
            .unwrap_or_default();
    }
    Ok(result)
}

pub async fn topup_status(profile: &Profile, trade_no: &str) -> Result<String, String> {
    if trade_no.is_empty()
        || trade_no.len() > 100
        || !trade_no.chars().all(|value| value.is_ascii_alphanumeric())
    {
        return Err("充值订单号无效。".into());
    }
    let (base, key) = credentials(profile)?;
    let url = base
        .join(&format!("api/usage/topup/order/{trade_no}"))
        .map_err(|_| "充值订单地址无效。".to_string())?;
    let value = response_data(
        client()?
            .get(url)
            .bearer_auth(key)
            .header("Accept", "application/json")
            .send()
            .await
            .map_err(|_| "无法连接充值接口。".to_string())?,
    )
    .await?;
    value["order"]["status"]
        .as_str()
        .map(str::to_owned)
        .ok_or_else(|| "充值接口未返回订单状态。".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn payment_method_survives_the_api_to_renderer_boundary() {
        let info: TopUpInfo = serde_json::from_value(json!({
            "enabled": true, "min_amount": 3, "amount_options": [5],
            "payment_methods": [{"name": "支付宝", "type": "alipay"}]
        }))
        .unwrap();
        let ipc = serde_json::to_value(info).unwrap();
        assert_eq!(ipc["minAmount"], 3);
        assert_eq!(ipc["paymentMethods"][0]["kind"], "alipay");
    }

    #[test]
    fn checkout_fields_survive_the_api_to_renderer_boundary() {
        let checkout: Checkout = serde_json::from_value(json!({
            "trade_no": "USR1NOtest123",
            "payment_method": "alipay",
            "qr_code": "https://pay.example/qr",
            "pay_url": "https://pay.example/checkout"
        }))
        .unwrap();
        let ipc = serde_json::to_value(checkout).unwrap();
        assert_eq!(ipc["tradeNo"], "USR1NOtest123");
        assert_eq!(ipc["paymentMethod"], "alipay");
        assert_eq!(ipc["qrCode"], "https://pay.example/qr");
        assert_eq!(ipc["payUrl"], "https://pay.example/checkout");
    }
}
