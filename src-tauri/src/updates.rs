use crate::{download::download, files::Result};
use serde::Deserialize;
use std::{path::Path, time::Duration};
use tauri::{ipc::Channel, AppHandle};
use tokio::sync::Mutex;

const RELEASE_API: &str = "https://api.github.com/repos/yansd001/ApiConfigTool/releases/tags/v1.0";
const DOWNLOAD_BASE: &str = "https://github.com/yansd001/ApiConfigTool/releases/download/v1.0";
static INSTALLING: Mutex<()> = Mutex::const_new(());

#[derive(Deserialize)]
struct Release {
    body: String,
}

struct Update {
    build: u64,
    asset: &'static str,
    sha256: String,
}

fn platform_asset() -> Result<&'static str> {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => Ok("ApiConfigTool.exe"),
        ("macos", "aarch64") => Ok("ApiConfigTool-macos-arm64-v1.0.zip"),
        ("macos", "x86_64") => Ok("ApiConfigTool-macos-x64-v1.0.zip"),
        _ => Err("此平台暂未提供自动更新包。".into()),
    }
}

fn parse_update(body: &str, current: u64, asset: &'static str) -> Result<Option<Update>> {
    let Some(number) = body.lines().find_map(|s| s.strip_prefix("build=")) else {
        // Releases published before the updater have no build number.
        return Ok(None);
    };
    let build = number
        .trim()
        .parse()
        .map_err(|_| "发布版本的构建号无效。")?;
    if build <= current {
        return Ok(None);
    }
    let prefix = format!("sha256[{asset}]=");
    let hash = body
        .lines()
        .find_map(|s| s.strip_prefix(&prefix))
        .map(str::trim)
        .filter(|s| s.len() == 64 && s.bytes().all(|c| c.is_ascii_hexdigit()))
        .ok_or("发布版本缺少当前平台的有效校验值，请稍后重试。")?;
    Ok(Some(Update {
        build,
        asset,
        sha256: hash.to_ascii_lowercase(),
    }))
}

async fn latest() -> Result<Option<Update>> {
    let asset = platform_asset()?;
    let release: Release = reqwest::Client::builder()
        .user_agent("ApiConfigTool-Updater")
        .timeout(Duration::from_secs(15))
        .build()
        .map_err(|e| e.to_string())?
        .get(RELEASE_API)
        .send()
        .await
        .and_then(reqwest::Response::error_for_status)
        .map_err(|e| format!("无法检查 GitHub 发布版本：{e}"))?
        .json()
        .await
        .map_err(|_| "无法读取 GitHub 发布信息。")?;
    let current = option_env!("VITE_APP_BUILD")
        .and_then(|n| n.parse().ok())
        .unwrap_or(0);
    parse_update(&release.body, current, asset)
}

#[tauri::command]
pub async fn check_for_updates() -> Result<Option<u64>> {
    Ok(latest().await?.map(|update| update.build))
}

#[tauri::command]
pub async fn install_update(app: AppHandle, build: u64, progress: Channel<String>) -> Result<()> {
    let _guard = INSTALLING.try_lock().map_err(|_| "正在安装更新。")?;
    let update = latest()
        .await?
        .filter(|update| update.build == build)
        .ok_or("发布版本已变更，请重新检查更新。")?;
    let temp = tempfile::tempdir().map_err(|e| format!("无法创建更新目录：{e}"))?;
    let package = temp.path().join(update.asset);
    let url = format!("{DOWNLOAD_BASE}/{}", update.asset);
    download(&url, &update.sha256, &package, "更新包", &progress).await?;
    let _ = progress.send("正在安装更新…".into());
    tokio::task::spawn_blocking(move || {
        let _temp = temp;
        replace_app(&package)
    })
    .await
    .map_err(|_| "安装任务中断。")??;
    app.request_restart();
    Ok(())
}

#[cfg(windows)]
fn replace_app(package: &Path) -> Result<()> {
    use std::fs;
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let dir = exe.parent().ok_or("无法定位程序目录。")?;
    let staging = tempfile::tempdir_in(dir).map_err(|e| format!("无法写入程序目录：{e}"))?;
    let new = staging.path().join("update.exe");
    let old = staging.path().join("previous.exe");
    fs::copy(package, &new).map_err(|e| format!("无法写入新版程序：{e}"))?;
    fs::rename(&exe, &old).map_err(|e| format!("无法替换正在运行的程序：{e}"))?;
    if let Err(e) = fs::rename(&new, &exe) {
        if fs::rename(&old, &exe).is_err() {
            let recovery = staging.keep().join("previous.exe");
            return Err(format!(
                "更新失败，请从 {} 恢复旧程序：{e}",
                recovery.display()
            ));
        }
        return Err(format!("更新失败，已恢复旧程序：{e}"));
    }
    // Windows holds the old executable open until this process exits.
    // Only schedule its removal after the new executable is in place.
    let _ = self_replace::self_delete_at(old);
    Ok(())
}

#[cfg(target_os = "macos")]
fn replace_app(package: &Path) -> Result<()> {
    use std::{ffi::CString, os::unix::ffi::OsStrExt, process::Command};
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let bundle = exe
        .ancestors()
        .nth(3)
        .filter(|p| {
            p.extension().is_some_and(|ext| ext == "app")
                && exe.starts_with(p.join("Contents/MacOS"))
        })
        .ok_or("请从已安装的 API Config Tool.app 中更新。")?;
    let staging = tempfile::tempdir_in(bundle.parent().ok_or("无法定位应用目录。")?)
        .map_err(|e| format!("无法写入应用目录，请先将应用移出磁盘映像：{e}"))?;
    let unpacked = Command::new("/usr/bin/ditto")
        .args(["-x", "-k"])
        .arg(package)
        .arg(staging.path())
        .status()
        .map_err(|e| format!("无法解压更新包：{e}"))?;
    let new_bundle = staging.path().join("API Config Tool.app");
    let binary = exe.strip_prefix(bundle).map_err(|_| "无法定位应用程序。")?;
    if !unpacked.success() || !new_bundle.join(binary).is_file() {
        return Err("更新包缺少完整的应用程序。".into());
    }
    let from = CString::new(new_bundle.as_os_str().as_bytes()).map_err(|e| e.to_string())?;
    let to = CString::new(bundle.as_os_str().as_bytes()).map_err(|e| e.to_string())?;
    // Both paths are on the same volume. Swap whole bundles so an interruption
    // cannot leave an empty installation or mix resources from different builds.
    if unsafe { libc::renamex_np(from.as_ptr(), to.as_ptr(), libc::RENAME_SWAP) } != 0 {
        return Err(format!(
            "无法替换应用程序：{}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(())
}

#[cfg(not(any(windows, target_os = "macos")))]
fn replace_app(_: &Path) -> Result<()> {
    Err("此平台暂未提供自动更新包。".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rolling_release_requires_a_newer_build_and_platform_digest() {
        let asset = "ApiConfigTool.exe";
        let hash = "ab".repeat(32);
        let body = format!("build=12\r\nsha256[{asset}]={hash}\r\n");
        assert_eq!(
            parse_update(&body, 11, asset).unwrap().unwrap().sha256,
            hash
        );
        assert!(parse_update(&body, 12, asset).unwrap().is_none());
        assert!(parse_update(&body, 13, asset).unwrap().is_none());
        assert!(parse_update("旧发布", 0, asset).unwrap().is_none());
        assert!(parse_update(&body, 0, "other.zip").is_err());
        assert!(parse_update("build=12\nsha256[ApiConfigTool.exe]=bad", 1, asset).is_err());
    }
}
