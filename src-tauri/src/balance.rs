//! Automatic New API balances and bounded, CC Switch-style custom queries.
use crate::{balance_script, config, files::Result, store::Profile};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    collections::HashMap,
    sync::Arc,
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tokio::sync::{Mutex, Semaphore};

#[derive(Clone, Copy, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum Mode {
    #[default]
    Auto,
    Custom,
    Off,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct BalanceConfig {
    pub mode: Mode,
    pub interval_minutes: u32,
    pub timeout_seconds: u32,
    pub script: String,
    pub base_url: String,
    pub api_key: String,
    pub access_token: String,
    pub user_id: String,
}
impl Default for BalanceConfig {
    fn default() -> Self {
        Self {
            mode: Mode::Auto,
            interval_minutes: 5,
            timeout_seconds: 15,
            script: String::new(),
            base_url: String::new(),
            api_key: String::new(),
            access_token: String::new(),
            user_id: String::new(),
        }
    }
}
pub fn validate_config(c: &BalanceConfig) -> Result<()> {
    if c.interval_minutes > 1440 || !(1..=60).contains(&c.timeout_seconds) {
        return Err("余额刷新间隔须为 0–1440 分钟，超时须为 1–60 秒。".into());
    }
    if c.script.len() > 64 * 1024
        || [&c.api_key, &c.access_token, &c.base_url, &c.user_id]
            .iter()
            .any(|s| s.len() > 8192)
    {
        return Err("余额查询配置过大。".into());
    }
    if c.mode == Mode::Custom && c.script.trim().is_empty() {
        return Err("请填写余额查询脚本。".into());
    }
    Ok(())
}

#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct BalanceRow {
    pub label: String,
    pub remaining: Option<f64>,
    pub used: Option<f64>,
    pub total: Option<f64>,
    pub unit: String,
    pub unlimited: bool,
    pub extra: String,
}
#[derive(Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct BalanceState {
    pub rows: Vec<BalanceRow>,
    pub note: String,
    pub error: Option<String>,
    pub updated_at: Option<u64>,
    pub next_retry_at: u64,
    pub can_refresh_at: u64,
}
#[derive(Default)]
struct Cached {
    state: BalanceState,
    attempted: bool,
}
pub struct BalanceService {
    cache: Mutex<HashMap<String, Arc<Mutex<Cached>>>>,
    parallel: Semaphore,
}
impl Default for BalanceService {
    fn default() -> Self {
        Self {
            cache: Mutex::new(HashMap::new()),
            parallel: Semaphore::new(3),
        }
    }
}
fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

impl BalanceService {
    pub async fn query(&self, profile: &Profile, force: bool) -> Result<BalanceState> {
        validate_config(&profile.balance)?;
        if profile.balance.mode == Mode::Off {
            return Ok(BalanceState::default());
        }
        let root = if profile.balance.mode == Mode::Auto {
            site_root(&profile.base_url)?.to_string()
        } else {
            profile.base_url.clone()
        };
        let key = serde_json::to_string(&(root, profile.api_key.trim(), &profile.balance))
            .map_err(|_| "余额配置无效。")?;
        let entry = {
            let mut cache = self.cache.lock().await;
            if cache.len() >= 256 {
                cache.retain(|_, v| Arc::strong_count(v) > 1);
            }
            cache.entry(key).or_default().clone()
        };
        // Identical queries (including another application's profile) share the result.
        let mut cached = entry.lock().await;
        let current = now();
        if cached.attempted
            && (current < cached.state.can_refresh_at
                || (!force
                    && (profile.balance.interval_minutes == 0
                        || current < cached.state.next_retry_at)))
        {
            return Ok(cached.state.clone());
        }
        if !force && profile.balance.interval_minutes == 0 {
            return Ok(cached.state.clone());
        }
        let _permit = self
            .parallel
            .acquire()
            .await
            .map_err(|_| "余额查询已停止。")?;
        let result = fetch(profile).await;
        let time = now();
        cached.attempted = true;
        cached.state.can_refresh_at = time + 10;
        match result {
            Ok((rows, note)) => {
                cached.state.rows = rows;
                cached.state.note = note;
                cached.state.error = None;
                cached.state.updated_at = Some(time);
                cached.state.next_retry_at =
                    time + u64::from(profile.balance.interval_minutes) * 60;
            }
            Err(error) => {
                cached.state.error = Some(error.message);
                cached.state.next_retry_at = time + error.retry;
                if error.rate_limited {
                    cached.state.can_refresh_at = time + error.retry;
                }
            }
        }
        Ok(cached.state.clone())
    }
}

struct QueryError {
    message: String,
    retry: u64,
    rate_limited: bool,
}
impl From<String> for QueryError {
    fn from(message: String) -> Self {
        Self {
            message,
            retry: 300,
            rate_limited: false,
        }
    }
}
impl From<&str> for QueryError {
    fn from(s: &str) -> Self {
        s.to_string().into()
    }
}
type QueryResult<T> = std::result::Result<T, QueryError>;

pub fn site_root(base: &str) -> Result<url::Url> {
    let mut url =
        url::Url::parse(&config::validate_url(base)?).map_err(|_| "余额查询地址无效。")?;
    let mut parts: Vec<_> = url
        .path()
        .trim_matches('/')
        .split('/')
        .filter(|p| !p.is_empty())
        .map(str::to_owned)
        .collect();
    if let Some(index) = parts.iter().rposition(|p| p.eq_ignore_ascii_case("v1")) {
        parts.truncate(index);
    } else if parts
        .last()
        .is_some_and(|p| matches!(p.as_str(), "models" | "messages" | "responses"))
    {
        parts.pop();
    }
    url.set_path(&format!("/{}/", parts.join("/")));
    if parts.is_empty() {
        url.set_path("/");
    }
    Ok(url)
}
fn request_url(value: &str) -> Result<url::Url> {
    let url = url::Url::parse(value).map_err(|_| "余额请求 URL 无效。")?;
    if !matches!(url.scheme(), "https" | "http")
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err("余额请求只支持不含用户名、密码和片段的 HTTP(S) URL。".into());
    }
    Ok(url)
}
async fn read_json(request: reqwest::RequestBuilder) -> QueryResult<Value> {
    let mut response = request
        .send()
        .await
        .map_err(|_| QueryError::from("无法连接余额接口，请检查地址、网络或超时设置。"))?;
    let code = response.status().as_u16();
    if !response.status().is_success() {
        let message = match code {
            401 => "Key 无效或已禁用".into(),
            403 => "查询被拒绝：权限或 IP 限制".into(),
            404 | 405 => "站点不支持此余额接口，可配置自定义查询".into(),
            429 => "查询过于频繁，稍后重试".into(),
            300..=399 => "余额接口发生重定向，请核对地址".into(),
            _ => format!("余额接口暂不可用（HTTP {code}）"),
        };
        let retry = if code == 429 {
            response
                .headers()
                .get("retry-after")
                .and_then(|h| h.to_str().ok())
                .and_then(|v| {
                    v.parse::<u64>().ok().or_else(|| {
                        httpdate::parse_http_date(v).ok().map(|date| {
                            date.duration_since(SystemTime::now())
                                .unwrap_or_default()
                                .as_secs()
                        })
                    })
                })
                .unwrap_or(300)
                .clamp(300, 3600)
        } else if matches!(code, 401 | 403 | 404 | 405 | 300..=399) {
            1800
        } else {
            300
        };
        return Err(QueryError {
            message,
            retry,
            rate_limited: code == 429,
        });
    }
    let mut data = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| QueryError::from("余额响应读取失败。"))?
    {
        if data.len() + chunk.len() > 256 * 1024 {
            return Err("余额响应超过 256 KB。".into());
        }
        data.extend(chunk);
    }
    serde_json::from_slice(&data).map_err(|_| "余额接口未返回有效 JSON。".into())
}
async fn fetch(p: &Profile) -> QueryResult<(Vec<BalanceRow>, String)> {
    let client = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(p.balance.timeout_seconds.into()))
        .build()
        .map_err(|_| QueryError::from("无法创建余额请求。"))?;
    if p.balance.mode == Mode::Custom {
        let owned = p.clone();
        let request = tauri::async_runtime::spawn_blocking(move || balance_script::request(&owned))
            .await
            .map_err(|_| QueryError::from("余额脚本运行失败。"))??;
        let url = request_url(&request.url)?;
        let method = match request.method.to_ascii_uppercase().as_str() {
            "GET" => reqwest::Method::GET,
            "POST" => reqwest::Method::POST,
            _ => return Err("余额查询仅支持 GET 或 POST 请求。".into()),
        };
        let mut builder = client
            .request(method, url)
            .header("Accept", "application/json");
        for (name, value) in request.headers {
            builder = builder.header(name, value);
        }
        if let Some(body) = request.body {
            builder = if let Some(text) = body.as_str() {
                builder.body(text.to_string())
            } else {
                builder.json(&body)
            };
        }
        let response = read_json(builder).await?;
        let owned = p.clone();
        let extracted = tauri::async_runtime::spawn_blocking(move || {
            balance_script::extract(&owned, &response)
        })
        .await
        .map_err(|_| QueryError::from("余额提取脚本运行失败。"))??;
        let rows = parse_custom(&extracted)?;
        // Remote strings are display-only and scrubbed before reaching the UI.
        Ok((scrub_rows(rows, p), String::new()))
    } else {
        if p.api_key.trim().is_empty() || p.api_key.chars().any(char::is_control) {
            return Err("请填写有效的 API Key。".into());
        }
        let root = site_root(&p.base_url)?;
        let usage = read_json(
            client
                .get(root.join("api/usage/token/").unwrap())
                .bearer_auth(p.api_key.trim())
                .header("Accept", "application/json"),
        )
        .await?;
        let (mut rows, mut note) = parse_usage(&usage)?;
        // Public site metadata never receives the provider's credentials.
        if let Ok(status) = read_json(
            client
                .get(root.join("api/status").unwrap())
                .timeout(Duration::from_secs(4)),
        )
        .await
        {
            if let Some((scale, unit)) = parse_format(&status) {
                for row in &mut rows {
                    row.remaining = row.remaining.map(|v| v * scale);
                    row.used = row.used.map(|v| v * scale);
                    row.total = row.total.map(|v| v * scale);
                    row.unit = unit.clone();
                }
            }
        }
        if rows.iter().any(|r| r.unit == "额度") {
            note.push_str(" 未取得站点金额换算参数，显示原始额度。");
        }
        if rows
            .iter()
            .flat_map(|r| [r.remaining, r.used, r.total])
            .flatten()
            .any(|n| !n.is_finite())
        {
            return Err("站点余额数值超出范围。".into());
        }
        Ok((scrub_rows(rows, p), note.trim().into()))
    }
}
fn number(value: &Value) -> Option<f64> {
    value
        .as_f64()
        .or_else(|| value.as_str()?.parse().ok())
        .filter(|v| v.is_finite())
}
fn required(data: &Value, key: &str) -> Result<f64> {
    number(&data[key]).ok_or_else(|| "余额响应缺少有效数值。".into())
}
fn parse_usage(root: &Value) -> Result<(Vec<BalanceRow>, String)> {
    if root["success"] == false || root["code"] == false {
        return Err("查询被拒绝，请检查 Key 或站点权限。".into());
    }
    let data = &root["data"];
    let unlimited = data["unlimited_quota"]
        .as_bool()
        .ok_or("未返回 New API 额度数据。")?;
    let remaining = required(data, "total_available")?;
    let used = required(data, "total_used")?;
    let mut rows = Vec::new();
    let account = data.get("user_quota").filter(|v| !v.is_null());
    if let Some(account) = account {
        rows.push(BalanceRow {
            label: "账号余额".into(),
            remaining: Some(required(account, "total_available")?),
            used: Some(required(account, "total_used")?),
            unit: "额度".into(),
            ..Default::default()
        });
    }
    rows.push(BalanceRow {
        label: "Key 余额".into(),
        remaining: if unlimited { None } else { Some(remaining) },
        used: Some(used),
        unlimited,
        unit: "额度".into(),
        ..Default::default()
    });
    let note = if account.is_none() {
        "账号余额未开放查询；可在 New API 令牌设置中开启“允许查询用户额度”。"
    } else {
        ""
    };
    Ok((rows, note.into()))
}
fn parse_format(root: &Value) -> Option<(f64, String)> {
    if root["success"] == false {
        return None;
    }
    let d = &root["data"];
    let unit = number(&d["quota_per_unit"]).filter(|v| *v > 0.)?;
    let display = d["quota_display_type"]
        .as_str()
        .unwrap_or(if d["display_in_currency"] == true {
            "USD"
        } else {
            "TOKENS"
        });
    let (rate, name) = match display {
        "USD" => (1., "USD".to_string()),
        "CNY" => (number(&d["usd_exchange_rate"])?, "CNY".to_string()),
        "CUSTOM" => {
            let name = d["custom_currency_symbol"].as_str()?;
            if name.is_empty() || name.len() > 24 || name.chars().any(char::is_control) {
                return None;
            }
            (number(&d["custom_currency_exchange_rate"])?, name.into())
        }
        _ => return None,
    };
    let scale = rate / unit;
    (rate > 0. && scale.is_finite() && scale > 0.).then_some((scale, name))
}
fn safe_text(value: Option<&Value>, fallback: &str, max: usize) -> String {
    value
        .and_then(Value::as_str)
        .unwrap_or(fallback)
        .chars()
        .filter(|c| !c.is_control())
        .take(max)
        .collect()
}
fn parse_custom(value: &Value) -> Result<Vec<BalanceRow>> {
    let items = if let Some(array) = value.as_array() {
        array.clone()
    } else {
        vec![value.clone()]
    };
    if items.is_empty() || items.len() > 20 {
        return Err("余额提取结果须包含 1–20 个条目。".into());
    }
    items
        .iter()
        .map(|v| {
            if !v.is_object() {
                return Err("余额提取结果必须是对象或对象数组。".into());
            }
            if v["isValid"] == false {
                return Err("自定义接口返回账号无效或查询失败，请检查凭据和提取脚本。".into());
            }
            let field = |key| -> Result<Option<f64>> {
                match v.get(key) {
                    None => Ok(None),
                    Some(n) => number(n)
                        .map(Some)
                        .ok_or_else(|| format!("提取结果 {key} 必须是有限数值。")),
                }
            };
            let used = field("used")?;
            let total = field("total")?;
            let remaining = field("remaining")?.or_else(|| total.zip(used).map(|(t, u)| t - u));
            if remaining.is_none() && used.is_none() && total.is_none() {
                return Err("提取结果缺少 remaining、used 或 total 数值。".into());
            }
            if remaining.is_some_and(|n| !n.is_finite()) {
                return Err("提取结果数值超出范围。".into());
            }
            Ok(BalanceRow {
                label: safe_text(v.get("planName"), "余额", 80),
                remaining,
                used,
                total,
                unit: safe_text(v.get("unit"), "额度", 16),
                extra: safe_text(v.get("extra"), "", 200),
                unlimited: false,
            })
        })
        .collect()
}
fn scrub_rows(mut rows: Vec<BalanceRow>, profile: &Profile) -> Vec<BalanceRow> {
    for row in &mut rows {
        for text in [&mut row.label, &mut row.unit, &mut row.extra] {
            for secret in [
                &profile.api_key,
                &profile.balance.api_key,
                &profile.balance.access_token,
            ] {
                if !secret.is_empty() {
                    *text = text.replace(secret, "[已隐藏]");
                }
            }
        }
    }
    rows
}

#[cfg(test)]
mod tests;
