use super::{wait_for, Client, Result};
use objc2::rc::autoreleasepool;
use objc2_app_kit::{NSRunningApplication, NSWorkspace};
use objc2_foundation::NSString;

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
                NSRunningApplication::runningApplicationsWithBundleIdentifier(&id).count() > 0
            })
            .collect())
    })
}

pub fn launch(client: Client, restart: bool) -> Result<()> {
    autoreleasepool(|_| {
        let id = NSString::from_str(bundle(client));
        let workspace = NSWorkspace::sharedWorkspace();
        let apps = NSRunningApplication::runningApplicationsWithBundleIdentifier(&id);
        let url = apps
            .firstObject()
            .and_then(|app| app.bundleURL())
            .or_else(|| workspace.URLForApplicationWithBundleIdentifier(&id))
            .ok_or_else(|| format!("未找到 {}，请先安装桌面应用。", client.name()))?;
        if restart && apps.count() > 0 {
            for app in apps.to_vec() {
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
