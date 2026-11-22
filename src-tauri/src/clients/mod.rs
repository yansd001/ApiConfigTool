use crate::files::Result;
use serde::{Deserialize, Serialize};
use std::time::{Duration, Instant};

#[cfg(target_os = "macos")]
#[path = "macos.rs"]
mod platform;
#[cfg(windows)]
#[path = "windows.rs"]
mod platform;

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Client {
    VSCode,
    ChatGPT,
    Claude,
}

impl Client {
    fn name(self) -> &'static str {
        match self {
            Self::VSCode => "VSCode",
            Self::ChatGPT => "ChatGPT",
            Self::Claude => "Claude",
        }
    }
}

fn wait_for(client: Client, running: bool) -> Result<()> {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        if platform::running()?.contains(&client) == running {
            return Ok(());
        }
        if Instant::now() >= deadline {
            let state = if running {
                "启动"
            } else {
                "退出，请处理应用中的提示后重试"
            };
            return Err(format!("{} 尚未{}。", client.name(), state));
        }
        std::thread::sleep(Duration::from_millis(250));
    }
}

#[tauri::command]
pub async fn running_clients() -> Result<Vec<Client>> {
    tauri::async_runtime::spawn_blocking(platform::running)
        .await
        .map_err(|_| "无法读取应用运行状态。")?
}

#[tauri::command]
pub async fn launch_client(client: Client, restart: bool) -> Result<()> {
    static LAUNCH: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());
    let _guard = LAUNCH
        .try_lock()
        .map_err(|_| "正在操作桌面应用，请稍候。")?;
    tauri::async_runtime::spawn_blocking(move || {
        platform::launch(client, restart)?;
        wait_for(client, true)
    })
    .await
    .map_err(|_| "无法打开桌面应用。")?
}

#[cfg(not(any(windows, target_os = "macos")))]
mod platform {
    use super::*;
    pub fn running() -> Result<Vec<Client>> {
        Ok(Vec::new())
    }
    pub fn launch(_: Client, _: bool) -> Result<()> {
        Err("此功能支持 Windows 和 macOS。".into())
    }
}
