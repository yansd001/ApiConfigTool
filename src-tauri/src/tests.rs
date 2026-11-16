use super::*;
use serde_json::json;
use std::fs;

fn profile() -> Profile {
    Profile {
        name: "测试提供商".into(),
        base_url: "https://api.example.com/v1".into(),
        api_key: "test-key".into(),
        model: "test-model".into(),
        ..Default::default()
    }
}
fn fixture() -> (tempfile::TempDir, Paths) {
    let dir = tempfile::tempdir().unwrap();
    let paths = Paths::test(dir.path());
    (dir, paths)
}

#[test]
fn codex_account_switch_preserves_fresh_login_and_can_return_to_api() {
    let (_dir, paths) = fixture();
    let auth_path = paths.codex.join("auth.json");
    write(
        &auth_path,
        r#"{"tokens":{"access_token":"first-login","refresh_token":"refresh"},"auth_mode":"chatgpt"}"#,
    );
    write(
        &paths.config(AppId::Codex),
        "# user comment\n[features]\nfast_mode = true\n",
    );
    let saved = paths.save(AppId::Codex, profile(), true).unwrap();
    assert!(!saved.account_mode);
    let fresh = r#"{"tokens":{"access_token":"fresh-login","refresh_token":"new-refresh"},"auth_mode":"chatgpt"}"#;
    write(&auth_path, fresh);
    let snapshot = paths.switch_to_account(AppId::Codex).unwrap();
    assert!(snapshot.account_mode);
    assert!(snapshot.active_id.is_none());
    assert_eq!(snapshot.profiles.len(), 1);
    assert_eq!(fs::read_to_string(&auth_path).unwrap(), fresh);
    let source = fs::read_to_string(paths.config(AppId::Codex)).unwrap();
    assert!(source.contains("# user comment"));
    assert!(source.contains("fast_mode = true"));
    assert!(!source.contains("test-key"));
    assert!(!source.contains("test-model"));
    assert!(source.contains("forced_login_method = \"chatgpt\""));
    let restored = paths
        .activate(AppId::Codex, &snapshot.profiles[0].id)
        .unwrap();
    assert!(!restored.account_mode);
    assert!(restored.active_id.is_some());
    assert_eq!(fs::read_to_string(auth_path).unwrap(), fresh);
    assert!(!fs::read_to_string(paths.config(AppId::Codex))
        .unwrap()
        .contains("forced_login_method"));
}

#[test]
fn codex_account_clears_default_profile_overrides_and_legacy_key_only_auth() {
    let (_dir, paths) = fixture();
    write(&paths.config(AppId::Codex), "profile = 'work'\nmodel_provider = 'custom'\nopenai_base_url = 'https://old.example'\n[profiles.work]\nmodel_provider = 'custom'\nmodel = 'api-model'\nsandbox_mode = 'workspace-write'\n[model_providers.custom]\nexperimental_bearer_token = 'old-key'\n[model_providers.openai]\nbase_url = 'https://legacy.example'\n[profiles.other]\nmodel_provider = 'openai'\n");
    write(
        &paths.codex.join("auth.json"),
        r#"{"OPENAI_API_KEY":"old-key","auth_mode":"apikey","other":true}"#,
    );
    assert!(!paths.list(AppId::Codex).unwrap().account_mode);
    assert!(paths.switch_to_account(AppId::Codex).unwrap().account_mode);
    let doc = config::toml(&fs::read_to_string(paths.config(AppId::Codex)).unwrap()).unwrap();
    assert_eq!(
        doc["profiles"]["work"]["sandbox_mode"].as_str(),
        Some("workspace-write")
    );
    assert!(doc["profiles"]["work"].get("model_provider").is_none());
    assert!(doc["profiles"]["work"].get("model").is_none());
    assert_eq!(
        doc["profiles"]["other"]["model_provider"].as_str(),
        Some("saved-openai")
    );
    assert_eq!(
        files::json(&fs::read_to_string(paths.codex.join("auth.json")).unwrap()).unwrap(),
        json!({"other":true})
    );
}

#[test]
fn claude_account_switch_preserves_credentials_and_unrelated_settings() {
    let (_dir, paths) = fixture();
    let credentials = r#"{"claudeAiOauth":{"accessToken":"live-login"}}"#;
    write(&paths.claude.join(".credentials.json"), credentials);
    write(
        &paths.config(AppId::Claude),
        r#"{"env":{"ANTHROPIC_API_KEY":"api-key","ANTHROPIC_BASE_URL":"https://old.example","ANTHROPIC_DEFAULT_SONNET_MODEL":"api-model","CLAUDE_CODE_USE_BEDROCK":"1","CLAUDE_CODE_USE_POWERSHELL_TOOL":"1","CLAUDE_CODE_OAUTH_TOKEN":"official-token","CUSTOM":"yes"},"apiKeyHelper":"helper","forceLoginMethod":"console","model":"custom-model","permissions":{"allow":["Read"]},"hooks":{}}"#,
    );
    assert!(!paths.list(AppId::Claude).unwrap().account_mode);
    assert!(paths.switch_to_account(AppId::Claude).unwrap().account_mode);
    let root = files::json(&fs::read_to_string(paths.config(AppId::Claude)).unwrap()).unwrap();
    assert_eq!(
        root["env"],
        json!({"CLAUDE_CODE_USE_POWERSHELL_TOOL":"1","CLAUDE_CODE_OAUTH_TOKEN":"official-token","CUSTOM":"yes"})
    );
    assert_eq!(root["permissions"], json!({"allow":["Read"]}));
    assert!(root.get("hooks").is_some());
    assert!(root.get("model").is_none());
    assert!(root.get("apiKeyHelper").is_none());
    assert_eq!(root["forceLoginMethod"], "claudeai");
    assert_eq!(
        fs::read_to_string(paths.claude.join(".credentials.json")).unwrap(),
        credentials
    );
    let saved = paths.save(AppId::Claude, profile(), true).unwrap();
    assert!(!saved.account_mode);
    assert!(saved.active_id.is_some());
    assert!(
        files::json(&fs::read_to_string(paths.config(AppId::Claude)).unwrap())
            .unwrap()
            .get("forceLoginMethod")
            .is_none()
    );
}

#[test]
fn desktop_account_switch_preserves_other_profiles_and_restores_api() {
    let (_dir, paths) = fixture();
    write(
        &paths.desktop_threep.join("configLibrary/_meta.json"),
        r#"{"entries":[{"id":"another","name":"Other"}],"appliedId":"another"}"#,
    );
    write(
        &paths.desktop.join("claude_desktop_config.json"),
        r#"{"mcpServers":{"keep":{}},"enterpriseConfig":{"inferenceGatewayApiKey":"legacy-key","policy":true}}"#,
    );
    let mut p = profile();
    p.model = "claude-sonnet-4-6".into();
    let saved = paths.save(AppId::Desktop, p, true).unwrap();
    assert!(!saved.account_mode);
    let selected = paths.switch_to_account(AppId::Desktop).unwrap();
    assert!(selected.account_mode);
    assert_eq!(selected.profiles.len(), 1);
    for directory in [&paths.desktop, &paths.desktop_threep] {
        let root =
            files::json(&fs::read_to_string(directory.join("claude_desktop_config.json")).unwrap())
                .unwrap();
        assert_eq!(root["deploymentMode"], "1p");
        assert!(root["enterpriseConfig"]
            .get("inferenceGatewayApiKey")
            .is_none());
    }
    let normal =
        files::json(&fs::read_to_string(paths.desktop.join("claude_desktop_config.json")).unwrap())
            .unwrap();
    assert_eq!(normal["enterpriseConfig"]["policy"], true);
    assert!(normal["mcpServers"].get("keep").is_some());
    let meta = files::json(
        &fs::read_to_string(paths.desktop_threep.join("configLibrary/_meta.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(meta["entries"].as_array().unwrap().len(), 1);
    assert_eq!(meta["appliedId"], "another");
    let profile_text = fs::read_to_string(paths.config(AppId::Desktop)).unwrap();
    assert!(!profile_text.contains("test-key"));
    assert!(!profile_text.contains("inferenceProvider"));
    let restored = paths
        .activate(AppId::Desktop, &selected.profiles[0].id)
        .unwrap();
    assert!(!restored.account_mode);
    assert!(restored.active_id.is_some());
}

#[test]
fn official_mode_does_not_claim_a_login_and_account_switch_validates_every_file() {
    let (_dir, paths) = fixture();
    for app in [AppId::Codex, AppId::Claude, AppId::Desktop] {
        assert!(paths.list(app).unwrap().account_mode);
        assert!(paths.switch_to_account(app).unwrap().account_mode);
    }
    assert!(!paths.codex.join("auth.json").exists());
    assert!(!paths.claude.join(".credentials.json").exists());
    write(
        &paths.desktop.join("claude_desktop_config.json"),
        r#"{"deploymentMode":"3p"}"#,
    );
    write(
        &paths.desktop_threep.join("configLibrary/_meta.json"),
        r#"{"entries":{}}"#,
    );
    let before = fs::read(paths.desktop.join("claude_desktop_config.json")).unwrap();
    assert!(paths.switch_to_account(AppId::Desktop).is_err());
    assert_eq!(
        fs::read(paths.desktop.join("claude_desktop_config.json")).unwrap(),
        before
    );
    write(&paths.config(AppId::Claude), r#"{"env":5}"#);
    assert!(paths.switch_to_account(AppId::Claude).is_err());
    assert!(!paths.list(AppId::Claude).unwrap().account_mode);
}

#[test]
fn account_switch_transaction_rolls_back_config_and_auth_on_late_failure() {
    let (_dir, paths) = fixture();
    write(
        &paths.config(AppId::Codex),
        "model_provider = 'custom'\nmodel = 'api-model'\n",
    );
    write(
        &paths.codex.join("auth.json"),
        r#"{"OPENAI_API_KEY":"old-key","tokens":{"access_token":"keep"}}"#,
    );
    let config_before = fs::read(paths.config(AppId::Codex)).unwrap();
    let auth_before = fs::read(paths.codex.join("auth.json")).unwrap();
    let mut edits = account::projection(&paths, AppId::Codex).unwrap();
    let store_path = paths.store(AppId::Codex);
    let mut store = files::Edit::load(store_path.clone()).unwrap();
    store.after = b"{}".to_vec();
    edits.push(store);
    let error = files::commit_using(&edits, &paths.state.join("backups"), |path, bytes| {
        if path == store_path {
            Err("injected store failure".into())
        } else {
            files::replace(path, bytes)
        }
    });
    assert!(error.is_err());
    assert_eq!(fs::read(paths.config(AppId::Codex)).unwrap(), config_before);
    assert_eq!(
        fs::read(paths.codex.join("auth.json")).unwrap(),
        auth_before
    );
}
fn write(path: &std::path::Path, text: &str) {
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::write(path, text).unwrap();
}

#[test]
fn save_does_not_activate_and_delete_keeps_live_config() {
    let (_dir, paths) = fixture();
    let snapshot = paths.save(AppId::Claude, profile(), false).unwrap();
    assert!(!paths.config(AppId::Claude).exists());
    assert!(snapshot.active_id.is_none());
    let id = &snapshot.profiles[0].id;
    assert_eq!(
        paths
            .activate(AppId::Claude, id)
            .unwrap()
            .active_id
            .as_ref(),
        Some(id)
    );
    let before = fs::read(paths.config(AppId::Claude)).unwrap();
    paths.delete(AppId::Claude, id).unwrap();
    assert_eq!(fs::read(paths.config(AppId::Claude)).unwrap(), before);
}
#[test]
fn codex_preserves_tokens_shared_settings_comments_and_custom_paths() {
    let (_dir, paths) = fixture();
    write(
        &paths.config(AppId::Codex),
        "# keep this comment\nmodel = 'old'\n[mcp_servers.example]\ncommand = 'example'\n",
    );
    write(
        &paths.codex.join("auth.json"),
        r#"{"tokens":{"access_token":"official-token"},"OPENAI_API_KEY":"old","auth_mode":"apikey"}"#,
    );
    let mut p = profile();
    p.base_url = "https://api.example.com/gateway/custom".into();
    let saved = paths.save(AppId::Codex, p, true).unwrap();
    assert!(saved.active_id.is_some());
    let text = fs::read_to_string(paths.config(AppId::Codex)).unwrap();
    assert!(text.contains("# keep this comment"));
    assert!(text.contains("mcp_servers.example"));
    assert!(text.contains("/gateway/custom"));
    assert!(text.contains("requires_openai_auth = true"));
    let auth = files::json(&fs::read_to_string(paths.codex.join("auth.json")).unwrap()).unwrap();
    assert_eq!(auth["tokens"]["access_token"], "official-token");
    assert!(auth.get("OPENAI_API_KEY").is_none());
}
#[test]
fn malformed_live_file_and_profile_conflict_are_never_overwritten() {
    let (_dir, paths) = fixture();
    let bad = "[env\nsecret = 'never print this'";
    write(&paths.config(AppId::Codex), bad);
    let error = paths.save(AppId::Codex, profile(), true).err().unwrap();
    assert!(!error.contains("never print"));
    assert_eq!(fs::read_to_string(paths.config(AppId::Codex)).unwrap(), bad);
    assert!(!paths.store(AppId::Codex).exists());
    write(
        &paths.config(AppId::Codex),
        "profile = 'work'\n[profiles.work]\nmodel_provider = 'another'\n",
    );
    assert!(paths.save(AppId::Codex, profile(), true).is_err());
}
#[test]
fn claude_preserves_other_settings_and_removes_competing_key() {
    let (_dir, paths) = fixture();
    write(
        &paths.config(AppId::Claude),
        r#"{"permissions":{"allow":["Read"]},"env":{"ANTHROPIC_API_KEY":"old","CUSTOM":"yes"}}"#,
    );
    let saved = paths.save(AppId::Claude, profile(), true).unwrap();
    assert!(saved.active_id.is_some());
    let root = files::json(&fs::read_to_string(paths.config(AppId::Claude)).unwrap()).unwrap();
    assert_eq!(root["env"]["CUSTOM"], "yes");
    assert!(root["env"].get("ANTHROPIC_API_KEY").is_none());
    assert_eq!(root["permissions"]["allow"], json!(["Read"]));
    let mut changed = root;
    changed["env"]["ANTHROPIC_AUTH_TOKEN"] = json!("external");
    fs::write(paths.config(AppId::Claude), files::encode(&changed)).unwrap();
    assert!(paths.list(AppId::Claude).unwrap().active_id.is_none());
}
#[test]
fn desktop_writes_real_config_library_and_checks_applied_profile() {
    let (_dir, paths) = fixture();
    write(
        &paths.desktop_threep.join("configLibrary/_meta.json"),
        r#"{"entries":[{"id":"another","name":"Existing"}],"appliedId":"another"}"#,
    );
    write(
        &paths.desktop.join("claude_desktop_config.json"),
        r#"{"mcpServers":{"existing":{}}}"#,
    );
    let mut p = profile();
    p.model = "claude-sonnet-4-6".into();
    let snapshot = paths.save(AppId::Desktop, p, true).unwrap();
    assert!(snapshot.active_id.is_some());
    let root = files::json(&fs::read_to_string(paths.config(AppId::Desktop)).unwrap()).unwrap();
    assert_eq!(
        root["inferenceGatewayBaseUrl"],
        "https://api.example.com/v1"
    );
    assert_eq!(root["inferenceModels"], json!(["claude-sonnet-4-6"]));
    let meta_path = paths.desktop_threep.join("configLibrary/_meta.json");
    let mut meta = files::json(&fs::read_to_string(&meta_path).unwrap()).unwrap();
    assert_eq!(meta["entries"].as_array().unwrap().len(), 2);
    meta["appliedId"] = json!("another");
    fs::write(meta_path, files::encode(&meta)).unwrap();
    assert!(paths.list(AppId::Desktop).unwrap().active_id.is_none());
    assert!(
        fs::read_to_string(paths.desktop.join("claude_desktop_config.json"))
            .unwrap()
            .contains("mcpServers")
    );
}
#[test]
fn desktop_validates_all_files_before_writing_and_allows_model_discovery() {
    let (_dir, paths) = fixture();
    write(
        &paths.desktop_threep.join("configLibrary/_meta.json"),
        r#"{"entries":{}}"#,
    );
    let mut p = profile();
    p.model.clear();
    assert!(paths.save(AppId::Desktop, p.clone(), true).is_err());
    assert!(!paths.config(AppId::Desktop).exists());
    write(&paths.desktop_threep.join("configLibrary/_meta.json"), "{}");
    paths.save(AppId::Desktop, p, true).unwrap();
    assert!(!fs::read_to_string(paths.config(AppId::Desktop))
        .unwrap()
        .contains("inferenceModels"));
    assert!(!config::desktop_model("gpt-example"));
    assert!(!config::desktop_model("claude-sonnet-"));
}
#[test]
fn explicit_source_is_validated_and_keeps_extra_fields() {
    let (_dir, paths) = fixture();
    let mut p = profile();
    p.config_text = Some(r#"{"env":{"ANTHROPIC_BASE_URL":"https://source.example","ANTHROPIC_API_KEY":"source-key"},"model":"source-model","custom":42}"#.into());
    let saved = paths.save(AppId::Claude, p, true).unwrap();
    assert_eq!(saved.profiles[0].api_key, "source-key");
    assert!(saved.active_id.is_some());
    let live = files::json(&fs::read_to_string(paths.config(AppId::Claude)).unwrap()).unwrap();
    assert_eq!(live["custom"], 42);
    assert!(files::json(r#"{"env":{"key":"a","key":"b"}}"#).is_err());
}
#[test]
fn legacy_profiles_migrate_without_modifying_the_original_store() {
    let (_dir, paths) = fixture();
    let legacy = r#"{"version":1,"profiles":[{"id":"old-id","name":"旧配置","base_url":"https://old.example","api_key":"old-key","model":"old-model","config_text":null}]}"#;
    let file = paths.claude.join("apiconfig_claude_state.json");
    write(&file, legacy);
    let snapshot = paths.list(AppId::Claude).unwrap();
    assert_eq!(snapshot.profiles[0].id, "old-id");
    paths
        .save(AppId::Claude, snapshot.profiles[0].clone(), false)
        .unwrap();
    assert_eq!(fs::read_to_string(file).unwrap(), legacy);
    let codex_legacy = legacy.replace("\"version\":1", "\"version\":2");
    let codex_file = paths.codex.join("apiconfig_state.json");
    write(&codex_file, &codex_legacy);
    let snapshot = paths.list(AppId::Codex).unwrap();
    assert_eq!(snapshot.profiles[0].id, "old-id");
    paths
        .save(AppId::Codex, snapshot.profiles[0].clone(), false)
        .unwrap();
    assert_eq!(fs::read_to_string(codex_file).unwrap(), codex_legacy);
    assert_eq!(
        files::json(&fs::read_to_string(paths.store(AppId::Codex)).unwrap()).unwrap()["version"],
        1
    );
}

#[test]
fn desktop_basic_changes_update_explicit_source_model() {
    let (_dir, paths) = fixture();
    let source = r#"{"inferenceProvider":"gateway","inferenceGatewayAuthScheme":"bearer","inferenceGatewayBaseUrl":"https://example.com","inferenceGatewayApiKey":"test-key","inferenceModels":["claude-sonnet-4-6"],"extra":true}"#;
    let mut p = config::parse(AppId::Desktop, source, profile()).unwrap();
    p.model = "claude-opus-4-6".into();
    let edits = config::projection(&paths, AppId::Desktop, &p).unwrap();
    let output = files::json(std::str::from_utf8(&edits[0].after).unwrap()).unwrap();
    assert_eq!(output["inferenceModels"], json!(["claude-opus-4-6"]));
    assert_eq!(output["extra"], true);
}

fn http_fixture(response: String) -> (String, std::thread::JoinHandle<String>) {
    use std::io::{Read, Write};
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let handle = std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        stream
            .set_read_timeout(Some(std::time::Duration::from_secs(5)))
            .unwrap();
        let mut request = Vec::new();
        let mut buffer = [0; 1024];
        while !request.windows(4).any(|w| w == b"\r\n\r\n") {
            let count = stream.read(&mut buffer).unwrap();
            if count == 0 {
                break;
            }
            request.extend_from_slice(&buffer[..count]);
        }
        stream.write_all(response.as_bytes()).unwrap();
        String::from_utf8(request).unwrap()
    });
    (url, handle)
}

#[test]
fn model_fetch_preserves_path_filters_desktop_ids_and_deduplicates() {
    let body =
        r#"{"data":[{"id":"claude-sonnet-4-6"},{"id":"gpt-example"},{"id":"claude-sonnet-4-6"}]}"#;
    let (url, server) = http_fixture(format!("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}", body.len(), body));
    let result = tauri::async_runtime::block_on(fetch_models(
        AppId::Desktop,
        format!("{url}/gateway/v1"),
        "fake-model-key".into(),
    ))
    .unwrap();
    assert_eq!(result, vec!["claude-sonnet-4-6"]);
    let request = server.join().unwrap();
    assert!(request.starts_with("GET /gateway/v1/models "));
    assert!(request
        .to_ascii_lowercase()
        .contains("authorization: bearer fake-model-key"));
}

#[test]
fn model_fetch_does_not_follow_redirects_or_echo_sensitive_bodies() {
    let body = "private response body";
    let (url, server) = http_fixture(format!("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/leak\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}", body.len(), body));
    let error =
        tauri::async_runtime::block_on(fetch_models(AppId::Codex, url, "fake-model-key".into()))
            .unwrap_err();
    assert_eq!(error, "模型接口返回 HTTP 302。");
    assert!(!error.contains(body));
    server.join().unwrap();
}
#[test]
fn import_deduplicates_and_does_not_write_live_files() {
    let (_dir, paths) = fixture();
    let source = r#"{"model":"my-model","env":{"ANTHROPIC_BASE_URL":"https://api.example","ANTHROPIC_AUTH_TOKEN":"test-key"},"custom":true}"#;
    write(&paths.config(AppId::Claude), source);
    paths.import(AppId::Claude).unwrap();
    let snapshot = paths.import(AppId::Claude).unwrap();
    assert_eq!(snapshot.profiles.len(), 1);
    assert!(snapshot.active_id.is_some());
    assert_eq!(
        fs::read_to_string(paths.config(AppId::Claude)).unwrap(),
        source
    );
}
#[test]
fn transaction_rolls_back_on_later_write_failure_and_backs_up() {
    let (dir, _) = fixture();
    let first = dir.path().join("first.json");
    let second = dir.path().join("second.json");
    write(&first, "before");
    let mut a = files::Edit::load(first.clone()).unwrap();
    a.after = b"after".to_vec();
    let mut b = files::Edit::load(second.clone()).unwrap();
    b.after = b"new".to_vec();
    let result = files::commit_using(&[a, b], &dir.path().join("backups"), |path, bytes| {
        if path == second {
            Err("injected failure".into())
        } else {
            files::replace(path, bytes)
        }
    });
    assert!(result.is_err());
    assert_eq!(fs::read_to_string(first).unwrap(), "before");
    assert!(!second.exists());
    assert_eq!(fs::read_dir(dir.path().join("backups")).unwrap().count(), 1);
}
#[test]
fn transaction_refuses_external_edits_and_lock_is_exclusive() {
    let (dir, paths) = fixture();
    let file = dir.path().join("config.json");
    write(&file, "old");
    let mut edit = files::Edit::load(file.clone()).unwrap();
    edit.after = b"new".to_vec();
    write(&file, "external");
    assert!(files::commit(&[edit], &dir.path().join("backups")).is_err());
    assert_eq!(fs::read_to_string(file).unwrap(), "external");
    let _guard = files::lock(&paths.state).unwrap();
    assert!(files::lock(&paths.state).is_err());
}

#[test]
fn balance_settings_roundtrip_and_import_preserve_them_without_polluting_clients() {
    let (_dir, paths) = fixture();
    let mut p = profile();
    p.balance.mode = balance::Mode::Custom;
    p.balance.script =
        "({request:{url:'{{baseUrl}}/balance'},extractor:r=>({remaining:r.balance})})".into();
    p.balance.access_token = "separate-panel-token".into();
    let saved = paths.save(AppId::Claude, p, true).unwrap();
    assert_eq!(
        saved.profiles[0].balance.access_token,
        "separate-panel-token"
    );
    let imported = paths.import(AppId::Claude).unwrap();
    assert_eq!(
        imported.profiles[0].balance.access_token,
        "separate-panel-token"
    );
    assert!(!fs::read_to_string(paths.config(AppId::Claude))
        .unwrap()
        .contains("separate-panel-token"));
    let old: Profile =
        serde_json::from_value(json!({"baseUrl":"https://example.com","apiKey":"key"})).unwrap();
    assert!(old.balance.mode == balance::Mode::Auto);
    assert_eq!(old.balance.interval_minutes, 5);
}
