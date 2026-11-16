//! Switch native clients back to their own account login, without managing OAuth.
//! Desktop's 1p restoration and Claude field ownership follow CC Switch's direct projection.
use crate::{
    config::{self, DESKTOP_ID},
    files::{self, Edit, Result},
    store::{AppId, Paths},
};
use serde_json::{json, Value};
use toml_edit::{value, Item, TableLike};

const CODEX_CONNECTION_FIELDS: &[&str] = &[
    "model_provider",
    "openai_base_url",
    "base_url",
    "experimental_bearer_token",
    "wire_api",
    "preferred_auth_method",
    "forced_login_method",
];
const CODEX_MODEL_FIELDS: &[&str] = &["model", "review_model", "model_catalog_json"];
const CLAUDE_PROTOCOL_FIELDS: &[&str] = &[
    "CLAUDE_CODE_USE_BEDROCK",
    "CLAUDE_CODE_USE_VERTEX",
    "CLAUDE_CODE_USE_FOUNDRY",
    "CLAUDE_CODE_USE_GATEWAY",
    "CLAUDE_CODE_USE_MANTLE",
    "CLAUDE_CODE_USE_ANTHROPIC_AWS",
    "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD",
];
const CLAUDE_TOP_FIELDS: &[&str] = &[
    "apiKeyHelper",
    "apiBaseUrl",
    "primaryModel",
    "smallFastModel",
    "apiKey",
    "model",
    "fallbackModel",
    "modelOverrides",
    "modelPicker",
    "advisorModel",
    "awsAuthRefresh",
    "awsCredentialExport",
    "gcpAuthRefresh",
];
const DESKTOP_GATEWAY_FIELDS: &[&str] = &[
    "inferenceProvider",
    "inferenceGatewayBaseUrl",
    "inferenceGatewayApiKey",
    "inferenceGatewayAuthScheme",
    "inferenceModels",
    "modelDiscoveryEnabled",
];

fn clean_codex_table(table: &mut dyn TableLike) {
    for key in CODEX_CONNECTION_FIELDS.iter().chain(CODEX_MODEL_FIELDS) {
        table.remove(key);
    }
}

fn codex(paths: &Paths) -> Result<Vec<Edit>> {
    let mut main = Edit::load(paths.config(AppId::Codex))?;
    let mut doc = config::toml(files::text(&main.before)?)?;
    let selected_profile = doc.get("profile").and_then(Item::as_str).map(str::to_owned);
    clean_codex_table(doc.as_table_mut());
    doc["forced_login_method"] = value("chatgpt");
    // Keep default-profile settings (sandbox, features, etc.) while removing the
    // overrides that would otherwise silently send requests back to the API.
    if let Some(name) = selected_profile {
        if let Some(profile) = doc.get_mut("profiles").and_then(|p| p.get_mut(&name)) {
            clean_codex_table(
                profile
                    .as_table_like_mut()
                    .ok_or("Codex profile 必须是 TOML 表。")?,
            );
        }
    }
    let mut renamed = Vec::new();
    if let Some(providers) = doc.get_mut("model_providers") {
        let providers = providers
            .as_table_like_mut()
            .ok_or("model_providers 必须是 TOML 表。")?;
        providers.remove("custom");
        // Codex rejects these reserved table IDs even when another provider is selected.
        for id in ["openai", "ollama", "lmstudio"] {
            if let Some(old) = providers.remove(id) {
                let mut name = format!("saved-{id}");
                while providers.contains_key(&name) {
                    name.push('-');
                }
                providers.insert(&name, old);
                renamed.push((id, name));
            }
        }
    }
    if let Some(profiles) = doc.get_mut("profiles").and_then(Item::as_table_like_mut) {
        for (_, entry) in profiles.iter_mut() {
            if let Some(table) = entry.as_table_like_mut() {
                for (old, new) in &renamed {
                    if table.get("model_provider").and_then(Item::as_str) == Some(*old) {
                        table.insert("model_provider", value(new));
                    }
                }
            }
        }
    }
    main.after = doc.to_string().into_bytes();
    let mut auth = Edit::load(paths.codex.join("auth.json"))?;
    let mut root = files::json_or_empty(&auth.before)?;
    let object = root.as_object_mut().unwrap();
    object.remove("OPENAI_API_KEY");
    if object.get("auth_mode").and_then(Value::as_str) == Some("apikey") {
        object.remove("auth_mode");
    }
    let mut edits = vec![main];
    if auth.before.is_some() && root != files::json_or_empty(&auth.before)? {
        auth.after = files::encode(&root);
        edits.push(auth);
    }
    // Existing (possibly refreshed) ChatGPT tokens are never replaced from a backup.
    Ok(edits)
}

fn claude_env_key(key: &str) -> bool {
    key.starts_with("ANTHROPIC_")
        || CLAUDE_PROTOCOL_FIELDS.contains(&key)
        || matches!(
            key,
            "CLAUDE_CODE_SUBAGENT_MODEL" | "CLAUDE_CODE_API_KEY_HELPER_TTL_MS"
        )
        || (key.starts_with("CLAUDE_CODE_SKIP_") && key.ends_with("_AUTH"))
}

fn claude(paths: &Paths) -> Result<Vec<Edit>> {
    let mut edit = Edit::load(paths.config(AppId::Claude))?;
    let mut root = files::json_or_empty(&edit.before)?;
    let object = root.as_object_mut().unwrap();
    for key in CLAUDE_TOP_FIELDS {
        object.remove(*key);
    }
    if let Some(fields) = object.get_mut("env") {
        let fields = fields.as_object_mut().ok_or("env 必须是 JSON 对象。")?;
        if fields.values().any(|v| !v.is_string()) {
            return Err("env 中的值必须是字符串。".into());
        }
        fields.retain(|key, _| !claude_env_key(key));
        if fields.is_empty() {
            object.remove("env");
        }
    }
    object.insert("forceLoginMethod".into(), json!("claudeai"));
    edit.after = files::encode(&root);
    // .credentials.json, OS credential stores and official OAuth env tokens are preserved.
    Ok(vec![edit])
}

fn desktop(paths: &Paths) -> Result<Vec<Edit>> {
    let mut edits = Vec::new();
    for directory in [&paths.desktop, &paths.desktop_threep] {
        let mut edit = Edit::load(directory.join("claude_desktop_config.json"))?;
        let mut root = files::json_or_empty(&edit.before)?;
        root["deploymentMode"] = json!("1p");
        if let Some(enterprise) = root.get_mut("enterpriseConfig") {
            let enterprise = enterprise
                .as_object_mut()
                .ok_or("enterpriseConfig 必须是 JSON 对象。")?;
            for key in DESKTOP_GATEWAY_FIELDS {
                enterprise.remove(*key);
            }
            enterprise.remove("disableDeploymentModeChooser");
            if enterprise.is_empty() {
                root.as_object_mut().unwrap().remove("enterpriseConfig");
            }
        }
        edit.after = files::encode(&root);
        edits.push(edit);
    }
    let mut meta = Edit::load(paths.desktop_threep.join("configLibrary/_meta.json"))?;
    if meta.before.is_some() {
        let mut root = files::json_or_empty(&meta.before)?;
        let mut next_id = None;
        if let Some(entries) = root.get_mut("entries") {
            let entries = entries
                .as_array_mut()
                .ok_or("Desktop configLibrary entries 必须为数组。")?;
            entries.retain(|entry| entry.get("id").and_then(Value::as_str) != Some(DESKTOP_ID));
            next_id = entries
                .iter()
                .find_map(|entry| entry.get("id").and_then(Value::as_str))
                .map(str::to_owned);
        }
        if root.get("appliedId").and_then(Value::as_str) == Some(DESKTOP_ID) {
            match next_id {
                Some(id) => root["appliedId"] = json!(id),
                None => {
                    root.as_object_mut().unwrap().remove("appliedId");
                }
            }
        }
        meta.after = files::encode(&root);
        edits.push(meta);
    }
    let mut profile = Edit::load(paths.config(AppId::Desktop))?;
    if profile.before.is_some() {
        let mut root = files::json_or_empty(&profile.before)?;
        for key in DESKTOP_GATEWAY_FIELDS {
            root.as_object_mut().unwrap().remove(*key);
        }
        profile.after = files::encode(&root);
        edits.push(profile);
    }
    Ok(edits)
}

pub fn projection(paths: &Paths, app: AppId) -> Result<Vec<Edit>> {
    match app {
        AppId::Codex => codex(paths),
        AppId::Claude => claude(paths),
        AppId::Desktop => desktop(paths),
    }
}

fn populated(value: Option<&Value>) -> bool {
    match value {
        None | Some(Value::Null) | Some(Value::Bool(false)) => false,
        Some(Value::String(s)) => !s.is_empty() && s != "0" && s != "false",
        _ => true,
    }
}

fn codex_uses_account(table: &dyn TableLike) -> bool {
    table
        .get("model_provider")
        .is_none_or(|v| v.as_str() == Some("openai"))
        && table
            .get("forced_login_method")
            .is_none_or(|v| v.as_str() == Some("chatgpt"))
        && ["openai_base_url", "base_url", "experimental_bearer_token"]
            .iter()
            .all(|k| !table.contains_key(k))
        && table.get("preferred_auth_method").and_then(Item::as_str) != Some("apikey")
}

pub fn is_active(paths: &Paths, app: AppId) -> Result<bool> {
    match app {
        AppId::Codex => {
            let bytes = files::read(&paths.config(app))?;
            let doc = config::toml(files::text(&bytes)?)?;
            if !codex_uses_account(doc.as_table()) {
                return Ok(false);
            }
            if doc
                .get("model_providers")
                .and_then(|p| p.get("openai"))
                .is_some()
            {
                return Ok(false);
            }
            if let Some(name) = doc.get("profile").and_then(Item::as_str) {
                if let Some(profile) = doc.get("profiles").and_then(|p| p.get(name)) {
                    if !codex_uses_account(
                        profile
                            .as_table_like()
                            .ok_or("Codex profile 必须是 TOML 表。")?,
                    ) {
                        return Ok(false);
                    }
                }
            }
            let auth = files::json_or_empty(&files::read(&paths.codex.join("auth.json"))?)?;
            Ok(!populated(auth.get("OPENAI_API_KEY")) && auth["auth_mode"] != "apikey")
        }
        AppId::Claude => {
            let root = files::json_or_empty(&files::read(&paths.config(app))?)?;
            if root["forceLoginMethod"] == "console"
                || [
                    "apiKey",
                    "apiKeyHelper",
                    "apiBaseUrl",
                    "awsAuthRefresh",
                    "awsCredentialExport",
                    "gcpAuthRefresh",
                ]
                .iter()
                .any(|key| populated(root.get(key)))
            {
                return Ok(false);
            }
            if let Some(fields) = root.get("env") {
                let fields = fields.as_object().ok_or("env 必须是 JSON 对象。")?;
                if fields.values().any(|v| !v.is_string()) {
                    return Err("env 中的值必须是字符串。".into());
                }
                if fields.iter().any(|(key, value)| {
                    (matches!(
                        key.as_str(),
                        "ANTHROPIC_BASE_URL"
                            | "ANTHROPIC_API_KEY"
                            | "ANTHROPIC_AUTH_TOKEN"
                            | "ANTHROPIC_CUSTOM_HEADERS"
                    ) || CLAUDE_PROTOCOL_FIELDS.contains(&key.as_str())
                        || (key.starts_with("CLAUDE_CODE_SKIP_") && key.ends_with("_AUTH")))
                        && populated(Some(value))
                }) {
                    return Ok(false);
                }
            }
            Ok(true)
        }
        AppId::Desktop => {
            for directory in [&paths.desktop, &paths.desktop_threep] {
                let root = files::json_or_empty(&files::read(
                    &directory.join("claude_desktop_config.json"),
                )?)?;
                if root.get("deploymentMode").is_some_and(|v| v != "1p") {
                    return Ok(false);
                }
                if let Some(enterprise) = root.get("enterpriseConfig") {
                    let enterprise = enterprise
                        .as_object()
                        .ok_or("enterpriseConfig 必须是 JSON 对象。")?;
                    if DESKTOP_GATEWAY_FIELDS
                        .iter()
                        .any(|key| populated(enterprise.get(*key)))
                    {
                        return Ok(false);
                    }
                }
            }
            Ok(true)
        }
    }
}
