// Direct client configuration. Third-party notices: licenses/THIRD-PARTY-NOTICES.txt.
use crate::{
    files::{self, Edit, Result},
    store::{AppId, Paths, Profile},
};
use serde_json::{json, Value};
use toml_edit::{value, DocumentMut, Item, Table};

// A separate profile ID avoids overwriting other Desktop profiles.
pub const DESKTOP_ID: &str = "50f0a33c-e463-4c45-9eb0-120a99381f21";

pub fn validate_url(input: &str) -> Result<String> {
    let trimmed = input.trim().trim_end_matches('/');
    let parsed =
        url::Url::parse(trimmed).map_err(|_| "请填写完整的 HTTP(S) Base URL。".to_string())?;
    if !matches!(parsed.scheme(), "https" | "http")
        || parsed.host_str().is_none()
        || !parsed.username().is_empty()
        || parsed.password().is_some()
        || parsed.query().is_some()
        || parsed.fragment().is_some()
        || trimmed.chars().any(char::is_whitespace)
    {
        return Err("Base URL 不能包含用户名、密码、查询参数或片段。".into());
    }
    Ok(trimmed.into())
}

pub fn validate(app: AppId, mut p: Profile) -> Result<Profile> {
    crate::balance::validate_config(&p.balance)?;
    p.base_url = validate_url(&p.base_url)?;
    p.api_key = p.api_key.trim().into();
    p.model = p.model.trim().into();
    p.name = p.name.trim().into();
    if p.api_key.is_empty() || p.api_key.chars().any(char::is_control) {
        return Err("请填写有效的 API Key。".into());
    }
    if p.model.chars().any(char::is_control) || (app != AppId::Desktop && p.model.is_empty()) {
        return Err("请填写有效的模型名称。".into());
    }
    if app == AppId::Desktop && !p.model.is_empty() && !desktop_model(&p.model) {
        return Err("Claude Desktop 直连需要 Claude 模型 ID，例如 claude-sonnet-4-6；留空使用服务端模型发现。".into());
    }
    if p.name.is_empty() {
        p.name = url::Url::parse(&p.base_url)
            .unwrap()
            .host_str()
            .unwrap()
            .into();
    }
    Ok(p)
}

// Accept model identifiers supported by Desktop's direct gateway configuration.
pub fn desktop_model(model: &str) -> bool {
    let normalized = model.trim().to_ascii_lowercase();
    if normalized.contains("[1m]") {
        return false;
    }
    let Some(tail) = normalized
        .strip_prefix("anthropic/claude-")
        .or_else(|| normalized.strip_prefix("claude-"))
    else {
        return false;
    };
    ["sonnet-", "opus-", "haiku-", "fable-"]
        .iter()
        .any(|prefix| {
            tail.strip_prefix(prefix)
                .is_some_and(|rest| !rest.is_empty())
        })
}

pub(crate) fn toml(input: &str) -> Result<DocumentMut> {
    input.parse::<DocumentMut>().map_err(|e| {
        let offset = e.span().map(|s| s.start).unwrap_or(0).min(input.len());
        format!(
            "TOML 语法错误，位于第 {} 行。",
            input.as_bytes()[..offset]
                .iter()
                .filter(|&&b| b == b'\n')
                .count()
                + 1
        )
    })
}
fn string(root: &Value, key: &str) -> String {
    root.get(key)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .into()
}
fn env(root: &mut Value) -> Result<&mut serde_json::Map<String, Value>> {
    let obj = root.as_object_mut().ok_or("配置必须是 JSON 对象。")?;
    let env = obj
        .entry("env")
        .or_insert_with(|| json!({}))
        .as_object_mut()
        .ok_or("env 必须是 JSON 对象。")?;
    if env.values().any(|v| !v.is_string()) {
        return Err("env 中的值必须是字符串。".into());
    }
    Ok(env)
}

pub fn parse(app: AppId, source: &str, mut p: Profile) -> Result<Profile> {
    match app {
        AppId::Codex => {
            let doc = toml(source)?;
            let id = doc
                .get("model_provider")
                .and_then(Item::as_str)
                .unwrap_or("openai");
            let provider = doc
                .get("model_providers")
                .and_then(|x| x.get(id))
                .ok_or("未找到自定义 Codex 提供商。")?;
            p.base_url = provider
                .get("base_url")
                .and_then(Item::as_str)
                .unwrap_or_default()
                .into();
            p.api_key = provider
                .get("experimental_bearer_token")
                .and_then(Item::as_str)
                .unwrap_or_default()
                .into();
            p.model = doc
                .get("model")
                .and_then(Item::as_str)
                .unwrap_or_default()
                .into();
            if !matches!(
                provider.get("wire_api").and_then(Item::as_str),
                Some("responses" | "chat")
            ) {
                return Err("wire_api 必须为 responses 或 chat。".into());
            }
            for field in [
                "env_key",
                "auth",
                "aws",
                "http_headers",
                "env_http_headers",
                "query_params",
            ] {
                if provider.get(field).is_some() {
                    return Err(
                        "请移除自定义 provider 中其他鉴权字段，仅保留 experimental_bearer_token。"
                            .into(),
                    );
                }
            }
            check_codex_profile(&doc, &p)?;
        }
        AppId::Claude => {
            let mut root = files::json(source)?;
            p.model = string(&root, "model");
            let fields = env(&mut root)?;
            if fields.contains_key("ANTHROPIC_AUTH_TOKEN")
                && fields.contains_key("ANTHROPIC_API_KEY")
            {
                return Err("ANTHROPIC_AUTH_TOKEN 与 ANTHROPIC_API_KEY 只能保留一个。".into());
            }
            p.base_url = fields
                .get("ANTHROPIC_BASE_URL")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .into();
            p.api_key = fields
                .get("ANTHROPIC_AUTH_TOKEN")
                .or_else(|| fields.get("ANTHROPIC_API_KEY"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .into();
        }
        AppId::Desktop => {
            let root = files::json(source)?;
            if root["inferenceProvider"] != "gateway"
                || root["inferenceGatewayAuthScheme"] != "bearer"
            {
                return Err("Claude Desktop 需要 gateway 提供商和 bearer 鉴权。".into());
            }
            p.base_url = string(&root, "inferenceGatewayBaseUrl");
            p.api_key = string(&root, "inferenceGatewayApiKey");
            p.model = match root.get("inferenceModels") {
                None => String::new(),
                Some(Value::Array(models))
                    if models.len() <= 200
                        && models.iter().all(|v| {
                            v.as_str()
                                .or_else(|| v.get("name").and_then(Value::as_str))
                                .is_some_and(desktop_model)
                        }) =>
                {
                    models
                        .first()
                        .and_then(|v| v.as_str().or_else(|| v.get("name").and_then(Value::as_str)))
                        .unwrap_or_default()
                        .into()
                }
                _ => {
                    return Err(
                        "inferenceModels 必须是有效的 Claude 模型列表（最多 200 个）。".into(),
                    )
                }
            };
        }
    }
    p.config_text = Some(source.into());
    validate(app, p)
}

fn check_codex_profile(doc: &DocumentMut, p: &Profile) -> Result<()> {
    if let Some(profile) = doc
        .get("profile")
        .and_then(Item::as_str)
        .and_then(|name| doc.get("profiles")?.get(name))
    {
        if profile
            .get("model_provider")
            .and_then(Item::as_str)
            .is_some_and(|v| v != "custom")
            || profile
                .get("model")
                .and_then(Item::as_str)
                .is_some_and(|v| v != p.model)
            || ["base_url", "openai_base_url"]
                .iter()
                .any(|k| profile.get(k).is_some())
        {
            return Err(
                "Codex 默认 profile 覆盖了提供商或模型，请先在 config.toml 中解决冲突。".into(),
            );
        }
    }
    Ok(())
}

pub fn projection(paths: &Paths, app: AppId, p: &Profile) -> Result<Vec<Edit>> {
    let mut main = Edit::load(paths.config(app))?;
    let source = p.config_text.as_deref();
    match app {
        AppId::Codex => {
            // Parse the live document even when applying an explicit source; corrupt files must not disappear.
            let live = toml(files::text(&main.before)?)?;
            let mut doc = if let Some(text) = source {
                toml(text)?
            } else {
                live
            };
            check_codex_profile(&doc, p)?;
            let mut auth = Edit::load(paths.codex.join("auth.json"))?;
            let mut auth_value = files::json_or_empty(&auth.before)?;
            let auth_obj = auth_value.as_object_mut().unwrap();
            auth_obj.remove("OPENAI_API_KEY");
            if auth_obj.get("auth_mode").and_then(Value::as_str) == Some("apikey") {
                auth_obj.remove("auth_mode");
            }
            let login = auth_obj
                .get("tokens")
                .and_then(Value::as_object)
                .is_some_and(|tokens| {
                    ["access_token", "refresh_token", "id_token"]
                        .iter()
                        .any(|k| {
                            tokens
                                .get(*k)
                                .and_then(Value::as_str)
                                .is_some_and(|s| !s.is_empty())
                        })
                });
            let original_id = doc
                .get("model_provider")
                .and_then(Item::as_str)
                .unwrap_or("custom")
                .to_string();
            let source_provider = if source.is_some() {
                doc.get("model_providers")
                    .and_then(|v| v.get(&original_id))
                    .cloned()
            } else {
                None
            };
            doc["model_provider"] = value("custom");
            doc.remove("forced_login_method");
            doc["model"] = value(&p.model);
            if source.is_none() && doc.contains_key("review_model") {
                doc["review_model"] = value(&p.model);
            }
            for key in [
                "preferred_auth_method",
                "openai_base_url",
                "base_url",
                "experimental_bearer_token",
                "wire_api",
            ] {
                doc.remove(key);
            }
            if !doc.contains_key("model_providers") {
                doc["model_providers"] = Item::Table(Table::new());
            }
            let providers = doc["model_providers"]
                .as_table_like_mut()
                .ok_or("model_providers 必须是 TOML 表。")?;
            let mut renamed = Vec::new();
            for reserved in ["openai", "ollama", "lmstudio"] {
                if let Some(old) = providers.remove(reserved) {
                    let mut name = format!("saved-{reserved}");
                    while providers.contains_key(&name) {
                        name.push('-');
                    }
                    providers.insert(&name, old);
                    renamed.push((reserved, name));
                }
            }
            let mut provider = source_provider.unwrap_or_else(|| Item::Table(Table::new()));
            let table = provider
                .as_table_like_mut()
                .ok_or("provider 必须是 TOML 表。")?;
            table.insert("name", value(&p.name));
            table.insert("base_url", value(&p.base_url));
            table.insert("experimental_bearer_token", value(&p.api_key));
            table.insert("requires_openai_auth", value(login));
            if !table.contains_key("wire_api") {
                table.insert("wire_api", value("responses"));
            }
            providers.insert("custom", provider);
            if let Some(profiles) = doc.get_mut("profiles").and_then(Item::as_table_like_mut) {
                for (_, profile) in profiles.iter_mut() {
                    if let Some(profile) = profile.as_table_like_mut() {
                        for (old, new) in &renamed {
                            if profile.get("model_provider").and_then(Item::as_str) == Some(*old) {
                                profile.insert("model_provider", value(new));
                            }
                        }
                    }
                }
            }
            main.after = doc.to_string().into_bytes();
            let mut edits = vec![main];
            if auth.before.is_some() && files::json_or_empty(&auth.before)? != auth_value {
                auth.after = files::encode(&auth_value);
                edits.push(auth);
            }
            Ok(edits)
        }
        AppId::Claude => {
            let live = files::json_or_empty(&main.before)?;
            let mut root = if let Some(s) = source {
                files::json(s)?
            } else {
                live
            };
            let fields = env(&mut root)?;
            let key_name = if source.is_some() && fields.contains_key("ANTHROPIC_API_KEY") {
                "ANTHROPIC_API_KEY"
            } else {
                "ANTHROPIC_AUTH_TOKEN"
            };
            fields.remove(if key_name == "ANTHROPIC_API_KEY" {
                "ANTHROPIC_AUTH_TOKEN"
            } else {
                "ANTHROPIC_API_KEY"
            });
            fields.insert(key_name.into(), p.api_key.clone().into());
            fields.insert("ANTHROPIC_BASE_URL".into(), p.base_url.clone().into());
            root["model"] = p.model.clone().into();
            root.as_object_mut().unwrap().remove("forceLoginMethod");
            main.after = files::encode(&root);
            Ok(vec![main])
        }
        AppId::Desktop => {
            let live = files::json_or_empty(&main.before)?;
            let mut profile = if let Some(s) = source {
                files::json(s)?
            } else {
                live
            };
            profile["inferenceProvider"] = json!("gateway");
            profile["inferenceGatewayAuthScheme"] = json!("bearer");
            profile["inferenceGatewayBaseUrl"] = json!(p.base_url);
            profile["inferenceGatewayApiKey"] = json!(p.api_key);
            profile["disableDeploymentModeChooser"] = json!(true);
            profile
                .as_object_mut()
                .unwrap()
                .entry("coworkEgressAllowedHosts")
                .or_insert(json!(["*"]));
            if source.is_none() {
                profile.as_object_mut().unwrap().remove("inferenceModels");
                if !p.model.is_empty() {
                    profile["inferenceModels"] = json!([p.model]);
                }
            } else {
                let first = profile
                    .get("inferenceModels")
                    .and_then(Value::as_array)
                    .and_then(|rows| rows.first())
                    .and_then(|v| v.as_str().or_else(|| v.get("name").and_then(Value::as_str)))
                    .unwrap_or_default();
                if first != p.model {
                    // Editing the basic model field updates the explicit source too.
                    if p.model.is_empty() {
                        profile.as_object_mut().unwrap().remove("inferenceModels");
                    } else {
                        profile["inferenceModels"] = json!([p.model]);
                    }
                }
            }
            main.after = files::encode(&profile);
            let mut edits = vec![main];
            for dir in [&paths.desktop, &paths.desktop_threep] {
                let mut edit = Edit::load(dir.join("claude_desktop_config.json"))?;
                let mut root = files::json_or_empty(&edit.before)?;
                root["deploymentMode"] = json!("3p");
                edit.after = files::encode(&root);
                edits.push(edit);
            }
            let mut meta = Edit::load(paths.desktop_threep.join("configLibrary/_meta.json"))?;
            let mut root = files::json_or_empty(&meta.before)?;
            let entries = root
                .as_object_mut()
                .unwrap()
                .entry("entries")
                .or_insert(json!([]))
                .as_array_mut()
                .ok_or("Desktop configLibrary entries 必须为数组。")?;
            if let Some(entry) = entries.iter_mut().find(|e| e["id"] == DESKTOP_ID) {
                entry["name"] = json!("API Config Tool");
            } else {
                entries.push(json!({"id": DESKTOP_ID, "name": "API Config Tool"}));
            }
            root["appliedId"] = json!(DESKTOP_ID);
            meta.after = files::encode(&root);
            edits.push(meta);
            Ok(edits)
        }
    }
}

pub fn active(paths: &Paths, app: AppId, profile: &Profile) -> bool {
    let Ok(expected) = projection(paths, app, profile) else {
        return false;
    };
    expected.iter().all(|edit| {
        let Some(before) = &edit.before else {
            return false;
        };
        if edit.path.extension().is_some_and(|x| x == "toml") {
            let parse = |bytes: &[u8]| {
                std::str::from_utf8(bytes)
                    .ok()
                    .and_then(|s| toml_edit::de::from_str::<Value>(s).ok())
            };
            parse(before).is_some() && parse(before) == parse(&edit.after)
        } else {
            let parse = |bytes: &[u8]| {
                std::str::from_utf8(bytes)
                    .ok()
                    .and_then(|s| files::json(s).ok())
            };
            parse(before).is_some() && parse(before) == parse(&edit.after)
        }
    })
}
