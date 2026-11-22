use super::{wait_for, Client, Result};
use objc2::rc::autoreleasepool;
use objc2_app_kit::{NSRunningApplication, NSWorkspace};
use objc2_foundation::NSString;
use std::path::PathBuf;

fn external_bundle(client: Client, path: Option<objc2::rc::Retained<NSString>>) -> bool {
    if client != Client::VSCode {
        return true;
    }
    let Some(path) = path else { return false };
    PathBuf::from(path.to_string())
        .parent()
        .is_some_and(|dir| !dir.join(crate::workspace::MANAGED_MARKER).is_file())
}

fn bundle(client: Client) -> &'static str {
    match client {
        Client::VSCode => "com.microsoft.VSCode",
        Client::ChatGPT => "com.openai.chat",
        Client::Claude => "com.anthropic.claudefordesktop",
    }
}

pub fn running() -> Result<Vec<Client>> {
    autoreleasepool(|_| {
        Ok([Client::VSCode, Client::ChatGPT, Client::Claude]
            .into_iter()
            .filter(|&client| {
                let id = NSString::from_str(bundle(client));
                NSRunningApplication::runningApplicationsWithBundleIdentifier(&id)
                    .to_vec()
                    .iter()
                    .any(|app| external_bundle(client, app.bundleURL().and_then(|url| url.path())))
            })
            .collect())
    })
}

pub fn launch(client: Client, restart: bool) -> Result<()> {
    autoreleasepool(|_| {
        let id = NSString::from_str(bundle(client));
        let workspace = NSWorkspace::sharedWorkspace();
        let apps: Vec<_> = NSRunningApplication::runningApplicationsWithBundleIdentifier(&id)
            .to_vec()
            .into_iter()
            .filter(|app| external_bundle(client, app.bundleURL().and_then(|url| url.path())))
            .collect();
        let url = apps
            .first()
            .and_then(|app| app.bundleURL())
            .or_else(|| {
                workspace
                    .URLForApplicationWithBundleIdentifier(&id)
                    .filter(|url| external_bundle(client, url.path()))
            })
            .ok_or_else(|| format!("未找到 {}，请先安装桌面应用。", client.name()))?;
        if restart && !apps.is_empty() {
            for app in apps {
                if !app.terminate() && !app.isTerminated() {
                    return Err(format!("无法退出 {}。", client.name()));
                }
            }
            wait_for(client, false)?;
        }
        if workspace.openURL(&url) {
            Ok(())
        } else {
            Err(format!("无法打开 {}。", client.name()))
        }
    })
}
