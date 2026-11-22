mod account;
mod balance;
mod balance_script;
mod config;
mod files;
mod recharge;
pub mod skills;
mod store;
pub mod terminal;
#[cfg(test)]
mod tests;
use files::Result;
use store::{AppId, Paths, Profile, Snapshot};
use tauri::{Manager, State};

#[tauri::command]
fn list_profiles(paths: State<Paths>, app: AppId) -> Result<Snapshot> {
    paths.list(app)
}
#[tauri::command]
fn save_profile(
    paths: State<Paths>,
    app: AppId,
    profile: Profile,
    activate: bool,
) -> Result<Snapshot> {
    paths.save(app, profile, activate)
}
#[tauri::command]
fn activate_profile(paths: State<Paths>, app: AppId, id: String) -> Result<Snapshot> {
    paths.activate(app, &id)
}
#[tauri::command]
fn switch_to_account(paths: State<Paths>, app: AppId) -> Result<Snapshot> {
    paths.switch_to_account(app)
}
#[tauri::command]
fn delete_profile(paths: State<Paths>, app: AppId, id: String) -> Result<Snapshot> {
    paths.delete(app, &id)
}
#[tauri::command]
fn import_profile(paths: State<Paths>, app: AppId) -> Result<Snapshot> {
    paths.import(app)
}
#[tauri::command]
fn preview_config(paths: State<Paths>, app: AppId, profile: Profile) -> Result<String> {
    let p = config::validate(app, profile)?;
    let edits = config::projection(&paths, app, &p)?;
    String::from_utf8(edits[0].after.clone()).map_err(|_| "配置编码无效。".into())
}
#[tauri::command]
fn parse_config(app: AppId, profile: Profile, source: String) -> Result<Profile> {
    config::parse(app, &source, profile)
}

#[tauri::command]
async fn fetch_balance(
    service: State<'_, balance::BalanceService>,
    profile: Profile,
    force: bool,
) -> std::result::Result<balance::BalanceState, String> {
    service.query(&profile, force).await
}

#[tauri::command]
async fn fetch_topup_info(profile: Profile) -> std::result::Result<recharge::TopUpInfo, String> {
    recharge::topup_info(&profile).await
}

#[tauri::command]
async fn create_topup(
    profile: Profile,
    amount: i64,
    payment_method: String,
) -> std::result::Result<recharge::Checkout, String> {
    recharge::create_topup(&profile, amount, &payment_method).await
}

#[tauri::command]
async fn fetch_topup_orders(
    profile: Profile,
) -> std::result::Result<Vec<recharge::PendingTopUp>, String> {
    recharge::topup_orders(&profile).await
}

#[tauri::command]
async fn resume_topup(
    profile: Profile,
    trade_no: String,
) -> std::result::Result<recharge::Checkout, String> {
    recharge::resume_topup(&profile, &trade_no).await
}

#[tauri::command]
async fn cancel_topup(profile: Profile, trade_no: String) -> std::result::Result<(), String> {
    recharge::cancel_topup(&profile, &trade_no).await
}

#[tauri::command]
async fn fetch_topup_status(
    profile: Profile,
    trade_no: String,
) -> std::result::Result<String, String> {
    recharge::topup_status(&profile, &trade_no).await
}

#[tauri::command]
async fn fetch_models(app: AppId, base_url: String, api_key: String) -> Result<Vec<String>> {
    let base_url = config::validate_url(&base_url)?;
    if api_key.trim().is_empty() || api_key.chars().any(char::is_control) {
        return Err("请填写有效的 API Key。".into());
    }
    let base = if url::Url::parse(&base_url)
        .unwrap()
        .path()
        .trim_matches('/')
        .is_empty()
    {
        format!("{base_url}/v1")
    } else {
        base_url
    };
    let client = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(std::time::Duration::from_secs(20))
        .build()
        .map_err(|_| "无法创建网络请求。")?;
    let mut request = client.get(format!("{base}/models")).bearer_auth(&api_key);
    if app != AppId::Codex {
        request = request
            .header("x-api-key", &api_key)
            .header("anthropic-version", "2023-06-01");
    }
    let mut response = request
        .send()
        .await
        .map_err(|_| "获取模型失败，请检查地址、网络或证书。")?;
    if !response.status().is_success() {
        return Err(format!(
            "模型接口返回 HTTP {}。",
            response.status().as_u16()
        ));
    }
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|_| "读取模型响应失败。")? {
        if bytes.len() + chunk.len() > 2 * 1024 * 1024 {
            return Err("模型响应超过 2 MB，已停止读取。".into());
        }
        bytes.extend(chunk);
    }
    let value: serde_json::Value =
        serde_json::from_slice(&bytes).map_err(|_| "模型接口没有返回有效的 JSON。")?;
    let rows = value
        .get("data")
        .or_else(|| value.get("models"))
        .and_then(|v| v.as_array())
        .ok_or("模型响应中缺少 data 列表。")?;
    let mut models: Vec<String> = rows
        .iter()
        .filter_map(|r| {
            r.get("id")
                .or_else(|| r.get("name"))?
                .as_str()
                .map(str::to_owned)
        })
        .filter(|id| app != AppId::Desktop || config::desktop_model(id))
        .collect();
    models.sort();
    models.dedup();
    if models.is_empty() {
        return Err("接口未返回适用于当前应用的模型，请手动填写。".into());
    }
    Ok(models)
}

pub fn run() {
    tauri::Builder::default()
        .manage(balance::BalanceService::default())
        .manage(terminal::TerminalManager::default())
        .plugin(
            tauri_plugin_opener::Builder::new()
                .open_js_links_on_click(false)
                .build(),
        )
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.set_focus();
            }
        }))
        .setup(|app| {
            app.manage(Paths::discover().map_err(std::io::Error::other)?);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_profiles,
            save_profile,
            activate_profile,
            switch_to_account,
            delete_profile,
            import_profile,
            preview_config,
            parse_config,
            fetch_models,
            fetch_balance,
            fetch_topup_info,
            create_topup,
            fetch_topup_orders,
            resume_topup,
            cancel_topup,
            fetch_topup_status,
            terminal::open_terminal,
            terminal::terminal_input,
            terminal::terminal_resize,
            terminal::close_terminal,
            skills::list_skills,
            skills::search_skills,
            skills::install_skill,
            skills::remove_skill
        ])
        .run(tauri::generate_context!())
        .expect("API Config Tool could not start");
}
