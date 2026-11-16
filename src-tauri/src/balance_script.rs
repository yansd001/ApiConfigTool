//! CC Switch-compatible request / extractor scripts, isolated from filesystem and host APIs.
use crate::{balance::BalanceConfig, files::Result, store::Profile};
use rquickjs::{Context, Function, Object, Runtime};
use serde::Deserialize;
use serde_json::Value;
use std::{
    collections::BTreeMap,
    time::{Duration, Instant},
};

#[derive(Deserialize)]
pub struct Request {
    pub url: String,
    #[serde(default = "get_method")]
    pub method: String,
    #[serde(default)]
    pub headers: BTreeMap<String, String>,
    pub body: Option<Value>,
}
fn get_method() -> String {
    "GET".into()
}

fn escape_literal(value: &str) -> String {
    // Escaping every UTF-16 code unit works in single/double/template literals and
    // prevents quotes, backticks or ${...} in credentials becoming executable code.
    value
        .encode_utf16()
        .map(|c| format!("\\u{c:04x}"))
        .collect()
}

fn source(profile: &Profile) -> String {
    let BalanceConfig {
        script,
        base_url,
        api_key,
        access_token,
        user_id,
        ..
    } = &profile.balance;
    let base = if base_url.trim().is_empty() {
        &profile.base_url
    } else {
        base_url
    };
    let key = if api_key.trim().is_empty() {
        &profile.api_key
    } else {
        api_key
    };
    let mut code = script.trim().trim_end_matches(';').to_string();
    for (name, value) in [
        ("baseUrl", base),
        ("apiKey", key),
        ("accessToken", access_token),
        ("userId", user_id),
    ] {
        code = code.replace(&format!("{{{{{name}}}}}"), &escape_literal(value.trim()));
    }
    code
}

fn execute(profile: &Profile, response: Option<&Value>, budget: Duration) -> Result<Value> {
    let runtime = Runtime::new().map_err(|_| "无法创建余额脚本运行环境。")?;
    runtime.set_memory_limit(16 * 1024 * 1024);
    runtime.set_max_stack_size(256 * 1024);
    let deadline = Instant::now() + budget;
    runtime.set_interrupt_handler(Some(Box::new(move || Instant::now() >= deadline)));
    let context = Context::full(&runtime).map_err(|_| "无法创建余额脚本上下文。")?;
    context.with(|ctx| {
        let object: Object = ctx
            .eval(source(profile))
            .map_err(|_| "余额脚本语法错误，或超出执行限制。")?;
        let result: rquickjs::Value = if let Some(response) = response {
            let extractor: Function = object
                .get("extractor")
                .map_err(|_| "余额脚本缺少 extractor 函数。")?;
            let input = ctx
                .json_parse(response.to_string())
                .map_err(|_| "余额响应无法转换为 JSON。")?;
            extractor
                .call((input,))
                .map_err(|_| "余额提取脚本执行失败，或超出执行限制。")?
        } else {
            let _: Function = object
                .get("extractor")
                .map_err(|_| "余额脚本缺少 extractor 函数。")?;
            object
                .get("request")
                .map_err(|_| "余额脚本缺少 request 对象。")?
        };
        let json = ctx
            .json_stringify(result)
            .map_err(|_| "余额脚本必须返回 JSON 数据。")?
            .ok_or("余额脚本未返回数据。")?;
        let text: String = json.to_string().map_err(|_| "余额脚本返回值无效。")?;
        if text.len() > 256 * 1024 {
            return Err("余额脚本返回值过大。".into());
        }
        serde_json::from_str(&text).map_err(|_| "余额脚本返回值无效。".into())
    })
}

pub fn request(profile: &Profile) -> Result<Request> {
    serde_json::from_value(execute(profile, None, Duration::from_secs(2))?)
        .map_err(|_| "余额脚本的 request 格式无效。".into())
}
pub fn extract(profile: &Profile, response: &Value) -> Result<Value> {
    execute(profile, Some(response), Duration::from_secs(2))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn compatible_placeholders_are_safe_in_all_string_quotes() {
        let key = "a'\"` ${throw new Error()}\\\n中文";
        let p = Profile { api_key: key.into(), base_url: "https://example.com/v1".into(), balance: BalanceConfig {
            script: r#"({request:{url:'{{baseUrl}}'.replace(/\/v1$/, '')+'/balance', headers:{one:'{{apiKey}}',two:"{{apiKey}}",three:`{{apiKey}}`}}, extractor: r => [{remaining:r.balance,unit:'USD'}]})"#.into(), ..Default::default()
        }, ..Default::default() };
        let request = request(&p).unwrap();
        assert_eq!(request.url, "https://example.com/balance");
        for value in request.headers.values() {
            assert_eq!(value, key);
        }
        assert_eq!(
            extract(&p, &json!({"balance":12})).unwrap()[0]["remaining"],
            12
        );
    }
    #[test]
    fn infinite_scripts_are_interrupted_and_cannot_access_host() {
        for script in [
            "(()=>{while(true){} })()",
            "({request:require('fs').readFileSync('x'),extractor:r=>r})",
        ] {
            let p = Profile {
                balance: BalanceConfig {
                    script: script.into(),
                    ..Default::default()
                },
                ..Default::default()
            };
            assert!(execute(&p, None, Duration::from_millis(30)).is_err());
        }
    }
}
