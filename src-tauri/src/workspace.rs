use crate::{download::download, files, store::Paths};
use jsonc_parser::cst::{CstInputValue, CstRootNode};
use serde::Deserialize;
use serde_json::json;
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
};
use tauri::{ipc::Channel, State};

type Result<T> = files::Result<T>;
const EXTENSIONS: [(&str, &str, &str); 3] = [
    ("codex", "openai.chatgpt", "Codex"),
    ("claude", "anthropic.claude-code", "Claude Code"),
    (
        "chinese",
        "ms-ceintl.vscode-language-pack-zh-hans",
        "简体中文语言包",
    ),
];
pub(super) const MANAGED_MARKER: &str = ".api-config-tool-editor";
const CATALOG: &str = include_str!("../workspace/packages.json");
mod profile;

#[derive(Deserialize)]
struct Package {
    url: String,
    sha256: String,
    version: Option<String>,
}

#[derive(Deserialize)]
struct Packages {
    #[serde(rename = "appResources")]
    app_resources: PathBuf,
    #[serde(flatten)]
    assets: HashMap<String, Package>,
}

struct Workspace {
    root: PathBuf,
    packages: Packages,
}

impl Workspace {
    fn new(paths: &Paths) -> Result<Self> {
        let platform = match (std::env::consts::OS, std::env::consts::ARCH) {
            ("windows", "x86_64") => "win32-x64",
            ("macos", "aarch64") => "darwin-arm64",
            ("macos", "x86_64") => "darwin-x64",
            _ => return Err("工作区支持 Windows x64 和 macOS Intel / Apple Silicon。".into()),
        };
        let mut catalog: HashMap<String, Packages> =
            serde_json::from_str(CATALOG).map_err(|_| "工作区安装清单无效。")?;
        Ok(Self {
            root: std::path::absolute(paths.state.join("vscode")).map_err(io_error)?,
            packages: catalog
                .remove(platform)
                .ok_or("缺少对应平台的工作区安装包。")?,
        })
    }

    fn component(&self, key: &str) -> PathBuf {
        self.root
            .join(format!("{key}-{}", &self.packages.assets[key].sha256[..16]))
    }
    fn executable(&self) -> PathBuf {
        self.component("runtime").join(if cfg!(windows) {
            "Code.exe"
        } else {
            "Visual Studio Code.app/Contents/MacOS/Code"
        })
    }
    fn resources(&self) -> PathBuf {
        self.component("runtime").join(&self.packages.app_resources)
    }
    fn cli(&self) -> PathBuf {
        self.resources().join("out/cli.js")
    }
    fn runtime_ready(&self) -> bool {
        self.executable().is_file()
            && self.cli().is_file()
            && self
                .resources()
                .join("extensions/git/package.json")
                .is_file()
    }
    fn git(&self) -> PathBuf {
        self.component("git").join(if cfg!(windows) {
            "cmd/git.exe"
        } else {
            "bin/git"
        })
    }
    fn installed_extensions(&self) -> Result<Vec<InstalledExtension>> {
        let path = self.root.join("extensions/extensions.json");
        let Some(bytes) = files::read(&path)? else {
            return Ok(Vec::new());
        };
        serde_json::from_slice(&bytes).map_err(|_| "无法读取内置 VS Code 扩展列表。".into())
    }
    fn extension_ready(&self, installed: &[InstalledExtension], key: &str, id: &str) -> bool {
        installed.iter().any(|entry| {
            entry.identifier.id.eq_ignore_ascii_case(id)
                && Some(entry.version.as_str()) == self.packages.assets[key].version.as_deref()
                && self
                    .root
                    .join("extensions")
                    .join(&entry.relative_location)
                    .join("package.json")
                    .is_file()
        })
    }
    fn ready(&self) -> Result<bool> {
        let installed = self.installed_extensions()?;
        Ok(self.runtime_ready()
            && self.git().is_file()
            && EXTENSIONS
                .iter()
                .all(|(key, id, _)| self.extension_ready(&installed, key, id)))
    }
    fn command(&self) -> Command {
        let mut cmd = quiet_command(self.executable());
        for (key, _) in std::env::vars_os() {
            let name = key.to_string_lossy().to_ascii_uppercase();
            if name.starts_with("VSCODE_")
                || name.starts_with("ELECTRON_")
                || name.starts_with("WEBVIEW2_")
                || name == "NODE_OPTIONS"
            {
                cmd.env_remove(key);
            }
        }
        // Portable mode keeps argv.json, user-data and extensions together in this private copy.
        cmd.env("VSCODE_PORTABLE", &self.root);
        if self.git().is_file() {
            let git = self.git();
            let mut paths = vec![git.parent().unwrap().to_path_buf()];
            paths.extend(std::env::split_paths(
                &std::env::var_os("PATH").unwrap_or_default(),
            ));
            if let Ok(path) = std::env::join_paths(paths) {
                cmd.env("PATH", path);
            }
            #[cfg(windows)]
            cmd.env(
                "CLAUDE_CODE_GIT_BASH_PATH",
                self.component("git").join("usr/bin/sh.exe"),
            );
        }
        cmd
    }
    fn configure(&self) -> Result<()> {
        let path = self.root.join("user-data/User/settings.json");
        if !path.exists() {
            files::replace(
                &path,
                &files::encode(&json!({
                    "git.path": self.git(),
                    "extensions.allowed": {
                        "*": false, "openai.chatgpt": true, "anthropic.claude-code": true,
                        "ms-ceintl.vscode-language-pack-zh-hans": true
                    },
                    "extensions.autoUpdate": false,
                    "update.mode": "none"
                })),
            )?;
        }
        set_editor_preferences(
            &path,
            &[
                ("chat.disableAIFeatures", true.into()),
                ("workbench.activity.showAccounts", false.into()),
                ("workbench.accounts.showAvatar", false.into()),
                ("workbench.startupEditor", "none".into()),
                ("chatgpt.openOnStartup", true.into()),
                ("security.workspace.trust.enabled", false.into()),
                (
                    "workbench.welcomePage.walkthroughs.openOnInstall",
                    false.into(),
                ),
                (
                    "extensions.allowed",
                    CstInputValue::Object(vec![
                        ("*".into(), false.into()),
                        ("openai.chatgpt".into(), true.into()),
                        ("anthropic.claude-code".into(), true.into()),
                        ("ms-ceintl.vscode-language-pack-zh-hans".into(), true.into()),
                    ]),
                ),
            ],
        )?;
        set_editor_preferences(&self.root.join("argv.json"), &[("locale", "zh-cn".into())])?;
        profile::configure_panels(&self.root)
    }
    fn install_vsix(&self, source: &Path) -> Result<()> {
        checked(
            self.command()
                .env("ELECTRON_RUN_AS_NODE", "1")
                .arg(self.cli())
                .arg("--install-extension")
                .arg(source)
                .arg("--force"),
            "安装 VS Code 扩展",
        )
    }
    fn open(&self, project: Option<&Path>) -> Result<()> {
        if !self.ready()? {
            return Err("请先完成内置 VS Code 和配套扩展的安装。".into());
        }
        {
            let _lock = files::lock(&self.root)?;
            self.configure()?;
        }
        let mut cmd = self.command();
        cmd.args(["--new-window", "--locale=zh-cn"]);
        if let Some(project) = project {
            if !project.is_absolute()
                || !(project.is_dir() || (profile::is_workspace_file(project) && project.is_file()))
            {
                return Err("找不到项目。请检查文件夹或工作区文件是否已移动。".into());
            }
            cmd.arg(project);
        }
        // Start Electron directly so its CLI cannot pass an English NLS environment to the window.
        let child = cmd
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| format!("打开内置 VS Code 失败：{e}"))?;
        std::thread::spawn(move || {
            let mut child = child;
            let _ = child.wait();
        });
        Ok(())
    }
}

fn set_editor_preferences(path: &Path, settings: &[(&str, CstInputValue)]) -> Result<()> {
    let original = files::read(path)?;
    let source = files::text(&original)?;
    let root = CstRootNode::parse(source, &Default::default())
        .map_err(|e| format!("无法更新 {}：{e}", path.display()))?;
    let object = match root.value() {
        None => root.object_value_or_set(),
        Some(_) => root
            .object_value()
            .ok_or_else(|| format!("{} 必须是 JSON 对象。", path.display()))?,
    };
    for (key, value) in settings {
        if let Some(prop) = object.get(key) {
            prop.set_value(value.clone());
        } else {
            object.append(key, value.clone());
        }
    }
    let updated = root.to_string();
    if updated != source {
        if files::read(path)? != original {
            return Err("编辑器设置刚被修改，请重试。".into());
        }
        let bom = original
            .as_deref()
            .is_some_and(|v| v.starts_with(b"\xef\xbb\xbf"));
        let bytes = if bom {
            format!("\u{feff}{updated}").into_bytes()
        } else {
            updated.into_bytes()
        };
        files::replace(path, &bytes)?;
    }
    Ok(())
}

#[derive(Deserialize)]
struct InstalledExtension {
    identifier: Identifier,
    version: String,
    #[serde(rename = "relativeLocation")]
    relative_location: String,
}
#[derive(Deserialize)]
struct Identifier {
    id: String,
}

fn io_error(e: std::io::Error) -> String {
    format!("工作区文件操作失败：{e}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn existing_editor_preferences_keep_comments_and_other_options() {
        let temp = tempfile::tempdir().unwrap();
        let ws = Workspace::new(&Paths::test(temp.path())).unwrap();
        let settings = ws.root.join("user-data/User/settings.json");
        files::replace(&settings, b"{\n // Custom setting\n \"editor.fontSize\": 18,\n \"chat.disableAIFeatures\": false,\n}\n").unwrap();
        ws.configure().unwrap();
        let updated = fs::read_to_string(&settings).unwrap();
        assert!(updated.contains("// Custom setting"));
        assert!(updated.contains("\"editor.fontSize\": 18"));
        assert!(updated.contains("\"chat.disableAIFeatures\": true"));
        assert!(updated.contains("\"*\": false"));
        assert!(updated.contains("\"openai.chatgpt\": true"));
        assert!(updated.contains("\"anthropic.claude-code\": true"));
        assert!(updated.contains("\"ms-ceintl.vscode-language-pack-zh-hans\": true"));
        assert_eq!(
            serde_json::from_slice::<serde_json::Value>(
                &fs::read(ws.root.join("argv.json")).unwrap()
            )
            .unwrap()["locale"],
            "zh-cn"
        );
        ws.configure().unwrap();
        assert_eq!(fs::read_to_string(&settings).unwrap(), updated);
    }
}

fn quiet_command(executable: impl AsRef<std::ffi::OsStr>) -> Command {
    let mut cmd = Command::new(executable);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x08000000);
    }
    cmd
}

fn checked(cmd: &mut Command, action: &str) -> Result<()> {
    let out = cmd.output().map_err(|e| format!("{action}失败：{e}"))?;
    if out.status.success() {
        return Ok(());
    }
    let stderr = String::from_utf8_lossy(&out.stderr);
    let stdout = String::from_utf8_lossy(&out.stdout);
    let detail = if stderr.trim().is_empty() {
        &stdout
    } else {
        &stderr
    };
    Err(format!(
        "{action}失败（{}）：{}",
        out.status,
        detail.trim().chars().take(500).collect::<String>()
    ))
}

fn unpack(archive: &Path, target: &Path, mac_zip: bool) -> Result<()> {
    #[cfg(windows)]
    let tar = PathBuf::from(std::env::var_os("SystemRoot").ok_or("找不到 Windows 系统目录。")?)
        .join("System32/tar.exe");
    #[cfg(not(windows))]
    let tar = PathBuf::from("/usr/bin/tar");
    let mut cmd = if mac_zip && cfg!(target_os = "macos") {
        let mut cmd = quiet_command("/usr/bin/ditto");
        cmd.args(["-x", "-k"]).arg(archive).arg(target);
        cmd
    } else {
        let mut cmd = quiet_command(tar);
        cmd.arg("-xf").arg(archive).arg("-C").arg(target);
        cmd
    };
    checked(&mut cmd, "解压工作区组件")
}


#[tauri::command]
pub fn workspace_status(paths: State<'_, Paths>) -> Result<bool> {
    Workspace::new(&paths)?.ready()
}

#[tauri::command]
pub async fn recent_workspace_projects(paths: State<'_, Paths>) -> Result<Vec<PathBuf>> {
    let root = Workspace::new(&paths)?.root;
    tokio::task::spawn_blocking(move || profile::recent_projects(&root))
        .await
        .map_err(|_| "读取最近项目的任务中断。")?
}

#[tauri::command]
pub async fn open_workspace(paths: State<'_, Paths>, project: Option<PathBuf>) -> Result<()> {
    let ws = Workspace::new(&paths)?;
    tokio::task::spawn_blocking(move || ws.open(project.as_deref()))
        .await
        .map_err(|_| "编辑器启动任务中断。")?
}

#[tauri::command]
pub async fn install_workspace(paths: State<'_, Paths>, progress: Channel<String>) -> Result<()> {
    let ws = Arc::new(Workspace::new(&paths)?);
    let _lock = files::lock(&ws.root)?;
    // A closed app can leave staging files. Under this lock they never belong to a live installer.
    let staging = ws.root.join("staging");
    if staging.exists() {
        fs::remove_dir_all(&staging).map_err(io_error)?;
    }
    fs::create_dir(&staging).map_err(io_error)?;
    let temp = tempfile::tempdir_in(staging).map_err(io_error)?;
    #[cfg(target_os = "macos")]
    if !ws.runtime_ready() {
        let version = Command::new("/usr/bin/sw_vers")
            .arg("-productVersion")
            .output()
            .map_err(io_error)?;
        if String::from_utf8_lossy(&version.stdout)
            .trim()
            .split('.')
            .next()
            .and_then(|v| v.parse::<u32>().ok())
            .is_some_and(|v| v < 12)
        {
            return Err("当前版本的工作区需要 macOS 12 或更高版本。".into());
        }
    }
    for (key, title, needed) in [
        ("runtime", "Visual Studio Code", !ws.runtime_ready()),
        ("git", "Git", !ws.git().is_file()),
    ] {
        if !needed {
            continue;
        }
        let _ = progress.send(format!("正在下载 {title}…"));
        let archive = temp.path().join(format!("{key}.archive"));
        let package = &ws.packages.assets[key];
        download(&package.url, &package.sha256, &archive, title, &progress).await?;
        let _ = progress.send(format!("正在解压 {title}…"));
        let extracted = temp.path().join(key);
        fs::create_dir(&extracted).map_err(io_error)?;
        let target = ws.component(key);
        let required = if key == "runtime" {
            ws.executable()
        } else {
            ws.git()
        };
        let relative = required.strip_prefix(&target).unwrap().to_path_buf();
        tokio::task::spawn_blocking(move || {
            unpack(&archive, &extracted, key == "runtime")?;
            if !extracted.join(relative).is_file() {
                return Err(format!("{title} 安装包缺少可执行文件。"));
            }
            if key == "runtime" {
                fs::write(extracted.join(MANAGED_MARKER), []).map_err(io_error)?;
            }
            if target.exists() {
                fs::remove_dir_all(&target).map_err(io_error)?;
            }
            fs::rename(extracted, target).map_err(io_error)
        })
        .await
        .map_err(|_| "工作区解压任务中断。")??;
    }
    ws.configure()?;
    let installed = ws.installed_extensions()?;
    for (key, id, title) in EXTENSIONS {
        if ws.extension_ready(&installed, key, id) {
            continue;
        }
        let _ = progress.send(format!("正在下载 {title}…"));
        let vsix = temp.path().join(format!("{key}.vsix"));
        let package = &ws.packages.assets[key];
        download(&package.url, &package.sha256, &vsix, title, &progress).await?;
        let _ = progress.send(format!("正在安装 {title}…"));
        // Native CLI installation only returns after the editor updates its extension registry.
        let installer = Arc::clone(&ws);
        tokio::task::spawn_blocking(move || {
            installer.install_vsix(&vsix)?;
            if !installer.extension_ready(&installer.installed_extensions()?, key, id) {
                return Err(format!("内置 VS Code 未能注册 {title}。"));
            }
            Ok::<(), String>(())
        })
        .await
        .map_err(|_| "扩展安装任务中断。")??;
    }
    if !ws.ready()? {
        return Err("内置 VS Code 安装不完整，请继续安装。".into());
    }
    Ok(())
}
