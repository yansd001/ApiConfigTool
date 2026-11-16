use crate::{
    account, config,
    files::{self, Edit, Result},
};
use serde::{Deserialize, Serialize};
#[cfg(target_os = "windows")]
use std::fs;
use std::path::PathBuf;

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AppId {
    #[serde(rename = "codex")]
    Codex,
    #[serde(rename = "claude")]
    Claude,
    #[serde(rename = "claude-desktop")]
    Desktop,
}
impl AppId {
    pub fn key(self) -> &'static str {
        match self {
            Self::Codex => "codex",
            Self::Claude => "claude",
            Self::Desktop => "claude-desktop",
        }
    }
}

#[derive(Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(alias = "base_url")]
    pub base_url: String,
    #[serde(alias = "api_key")]
    pub api_key: String,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub notes: String,
    #[serde(default, alias = "config_text")]
    pub config_text: Option<String>,
    #[serde(default)]
    pub balance: crate::balance::BalanceConfig,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Store {
    version: u32,
    profiles: Vec<Profile>,
    #[serde(default, alias = "active_profile_id")]
    active_id: Option<String>,
    #[serde(skip)]
    before: Option<Vec<u8>>,
}
impl Default for Store {
    fn default() -> Self {
        Self {
            version: 1,
            profiles: Vec::new(),
            active_id: None,
            before: None,
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub profiles: Vec<Profile>,
    pub active_id: Option<String>,
    pub account_mode: bool,
    pub config_path: String,
    pub store_path: String,
    pub warning: Option<String>,
}

#[derive(Clone)]
pub struct Paths {
    pub state: PathBuf,
    pub codex: PathBuf,
    pub claude: PathBuf,
    pub desktop: PathBuf,
    pub desktop_threep: PathBuf,
}
impl Paths {
    pub fn discover() -> Result<Self> {
        let user_dir = dirs::home_dir().ok_or("无法确定用户目录。")?;
        let override_path = |name, fallback| {
            std::env::var_os(name)
                .filter(|v| !v.is_empty())
                .map(PathBuf::from)
                .unwrap_or(fallback)
        };
        #[cfg(target_os = "windows")]
        let desktop_base = override_path(
            "LOCALAPPDATA",
            dirs::data_local_dir().unwrap_or_else(|| user_dir.join("AppData/Local")),
        );
        #[cfg(target_os = "macos")]
        let desktop_base = user_dir.join("Library/Application Support");
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        let desktop_base = dirs::config_dir().unwrap_or_else(|| user_dir.join(".config"));
        let desktop_dir = |name: &str| {
            let exact = desktop_base.join(name);
            if exact.exists() {
                return exact;
            }
            // CC Switch also recognizes versioned Windows Claude installations.
            #[cfg(target_os = "windows")]
            if let Ok(entries) = fs::read_dir(&desktop_base) {
                let mut candidates: Vec<_> = entries
                    .filter_map(std::result::Result::ok)
                    .map(|e| e.path())
                    .filter(|p| {
                        p.is_dir()
                            && p.file_name().and_then(|n| n.to_str()).is_some_and(|n| {
                                n.starts_with("Claude") && n.contains("-3p") == name.contains("-3p")
                            })
                    })
                    .collect();
                candidates.sort();
                if let Some(path) = candidates.first() {
                    return path.clone();
                }
            }
            exact
        };
        Ok(Self {
            state: override_path("API_CONFIG_TOOL_HOME", user_dir.join(".api-config-tool")),
            codex: override_path("CODEX_HOME", user_dir.join(".codex")),
            claude: override_path("CLAUDE_CONFIG_DIR", user_dir.join(".claude")),
            desktop: desktop_dir("Claude"),
            desktop_threep: desktop_dir("Claude-3p"),
        })
    }
    pub fn config(&self, app: AppId) -> PathBuf {
        match app {
            AppId::Codex => self.codex.join("config.toml"),
            AppId::Claude => self.claude.join("settings.json"),
            AppId::Desktop => self
                .desktop_threep
                .join(format!("configLibrary/{}.json", config::DESKTOP_ID)),
        }
    }
    pub fn store(&self, app: AppId) -> PathBuf {
        self.state.join(format!("{}.json", app.key()))
    }
    fn load(&self, app: AppId) -> Result<Store> {
        let bytes = files::read(&self.store(app))?;
        let mut store: Store = if bytes.is_some() {
            let value = files::json(files::text(&bytes)?)?;
            serde_json::from_value(value)
                .map_err(|_| "配置库结构无效，原文件已保留。".to_string())?
        } else {
            let legacy = match app {
                AppId::Codex => Some(self.codex.join("apiconfig_state.json")),
                AppId::Claude => Some(self.claude.join("apiconfig_claude_state.json")),
                AppId::Desktop => None,
            };
            let value = legacy.as_deref().map(files::read).transpose()?.flatten();
            if value.is_some() {
                let mut value = files::json(files::text(&value)?)?;
                // Old account-only stores have no profiles. No credentials are removed or overwritten.
                if value.get("profiles").is_some() {
                    let expected = if app == AppId::Codex { 2 } else { 1 };
                    if value["version"].as_u64() != Some(expected) {
                        return Err("旧版配置库版本不受支持。".into());
                    }
                    value["version"] = serde_json::json!(1);
                    serde_json::from_value(value)
                        .map_err(|_| "旧版配置库结构无效，无法导入。".to_string())?
                } else {
                    Store::default()
                }
            } else {
                Store::default()
            }
        };
        if store.version != 1 {
            return Err("配置库版本不受支持。".into());
        }
        let mut ids = std::collections::HashSet::new();
        for profile in &mut store.profiles {
            if profile.id.is_empty() || !ids.insert(profile.id.clone()) {
                return Err("配置库含空白或重复 ID。".into());
            }
            // Keep incomplete legacy entries editable; validate before every save and activation.
        }
        store.before = bytes;
        Ok(store)
    }
    fn store_edit(&self, app: AppId, store: &Store) -> Result<Edit> {
        Ok(Edit {
            path: self.store(app),
            before: store.before.clone(),
            after: files::encode(&serde_json::to_value(store).map_err(|_| "配置库序列化失败。")?),
        })
    }
    pub fn list(&self, app: AppId) -> Result<Snapshot> {
        let _guard = files::lock(&self.state)?;
        self.snapshot(app)
    }
    fn snapshot(&self, app: AppId) -> Result<Snapshot> {
        let store = self.load(app)?;
        let account_state = account::is_active(self, app);
        let account_mode = account_state.as_ref().copied().unwrap_or(false);
        let matches: Vec<_> = store
            .profiles
            .iter()
            .filter(|p| config::active(self, app, p))
            .collect();
        let active = matches
            .iter()
            .find(|p| Some(&p.id) == store.active_id.as_ref())
            .or_else(|| matches.first())
            .map(|p| p.id.clone());
        let warning = account_state.err().or_else(|| {
            if let Some(profile) = store.profiles.first().filter(|_| !account_mode) {
                config::projection(self, app, profile).err()
            } else {
                None
            }
        });
        Ok(Snapshot {
            profiles: store.profiles,
            active_id: if account_mode { None } else { active },
            account_mode,
            config_path: self.config(app).display().to_string(),
            store_path: self.store(app).display().to_string(),
            warning,
        })
    }
    pub fn switch_to_account(&self, app: AppId) -> Result<Snapshot> {
        let _guard = files::lock(&self.state)?;
        let mut store = self.load(app)?;
        let mut edits = account::projection(self, app)?;
        store.active_id = None;
        edits.push(self.store_edit(app, &store)?);
        files::commit(&edits, &self.state.join("backups"))?;
        self.snapshot(app)
    }
    pub fn save(&self, app: AppId, profile: Profile, activate: bool) -> Result<Snapshot> {
        let _guard = files::lock(&self.state)?;
        let mut store = self.load(app)?;
        let mut profile = if let Some(source) = profile.config_text.clone() {
            config::parse(app, &source, profile)?
        } else {
            config::validate(app, profile)?
        };
        if profile.id.is_empty() {
            profile.id = uuid::Uuid::new_v4().to_string();
            store.profiles.push(profile.clone());
        } else {
            let row = store
                .profiles
                .iter_mut()
                .find(|p| p.id == profile.id)
                .ok_or("配置已被删除，请刷新后重试。")?;
            *row = profile.clone();
        }
        let mut edits = if activate {
            config::projection(self, app, &profile)?
        } else {
            Vec::new()
        };
        if activate {
            store.active_id = Some(profile.id.clone());
        }
        edits.push(self.store_edit(app, &store)?);
        files::commit(&edits, &self.state.join("backups"))?;
        self.snapshot(app)
    }
    pub fn activate(&self, app: AppId, id: &str) -> Result<Snapshot> {
        let _guard = files::lock(&self.state)?;
        let mut store = self.load(app)?;
        let p = store
            .profiles
            .iter()
            .find(|p| p.id == id)
            .ok_or("配置不存在。")?
            .clone();
        let p = if let Some(source) = p.config_text.clone() {
            config::parse(app, &source, p)?
        } else {
            config::validate(app, p)?
        };
        let mut edits = config::projection(self, app, &p)?;
        store.active_id = Some(id.into());
        edits.push(self.store_edit(app, &store)?);
        files::commit(&edits, &self.state.join("backups"))?;
        self.snapshot(app)
    }
    pub fn delete(&self, app: AppId, id: &str) -> Result<Snapshot> {
        let _guard = files::lock(&self.state)?;
        let mut store = self.load(app)?;
        let before = store.profiles.len();
        store.profiles.retain(|p| p.id != id);
        if before == store.profiles.len() {
            return Err("配置不存在。".into());
        }
        if store.active_id.as_deref() == Some(id) {
            store.active_id = None;
        }
        files::commit(
            &[self.store_edit(app, &store)?],
            &self.state.join("backups"),
        )?;
        self.snapshot(app)
    }
    pub fn import(&self, app: AppId) -> Result<Snapshot> {
        let _guard = files::lock(&self.state)?;
        let mut config_path = self.config(app);
        if app == AppId::Desktop {
            let meta = files::json_or_empty(&files::read(
                &self.desktop_threep.join("configLibrary/_meta.json"),
            )?)?;
            if let Some(id) = meta["appliedId"].as_str() {
                if uuid::Uuid::parse_str(id).is_err() {
                    return Err("Desktop 当前配置 ID 无效。".into());
                }
                config_path = self.desktop_threep.join(format!("configLibrary/{id}.json"));
            }
        }
        let bytes = files::read(&config_path)?;
        if bytes.is_none() {
            return Err("未找到本机 API 配置，请先添加自定义提供商。".into());
        }
        let mut source = files::text(&bytes)?.to_string();
        if app == AppId::Codex {
            let mut doc = source
                .parse::<toml_edit::DocumentMut>()
                .map_err(|_| "本机 TOML 语法无效。")?;
            let id = doc
                .get("model_provider")
                .and_then(toml_edit::Item::as_str)
                .unwrap_or("openai")
                .to_string();
            if let Some(provider) = doc
                .get_mut("model_providers")
                .and_then(|p| p.get_mut(&id))
                .and_then(toml_edit::Item::as_table_like_mut)
            {
                if !provider.contains_key("experimental_bearer_token") {
                    let auth = files::json_or_empty(&files::read(&self.codex.join("auth.json"))?)?;
                    if let Some(key) = auth["OPENAI_API_KEY"].as_str() {
                        provider.insert("experimental_bearer_token", toml_edit::value(key));
                    }
                }
            }
            source = doc.to_string();
        }
        let mut p = config::parse(
            app,
            &source,
            Profile {
                name: "本机配置".into(),
                ..Default::default()
            },
        )?;
        let mut store = self.load(app)?;
        if let Some(existing) = store
            .profiles
            .iter_mut()
            .find(|x| x.base_url == p.base_url && x.api_key == p.api_key && x.model == p.model)
        {
            p.id = existing.id.clone();
            p.name = existing.name.clone();
            p.notes = existing.notes.clone();
            p.balance = existing.balance.clone();
            *existing = p;
        } else {
            p.id = uuid::Uuid::new_v4().to_string();
            store.profiles.push(p);
        }
        files::commit(
            &[self.store_edit(app, &store)?],
            &self.state.join("backups"),
        )?;
        self.snapshot(app)
    }
}

#[cfg(test)]
impl Paths {
    pub fn test(root: &std::path::Path) -> Self {
        Self {
            state: root.join("store"),
            codex: root.join("codex"),
            claude: root.join("claude"),
            desktop: root.join("Claude"),
            desktop_threep: root.join("Claude-3p"),
        }
    }
}
