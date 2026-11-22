use super::{wait_for, Client, Result};
use std::{
    ffi::OsString,
    os::windows::{ffi::OsStringExt, process::CommandExt},
    path::{Path, PathBuf},
    process::Command,
};
use windows::{
    core::{Owned, HSTRING, PCWSTR, PWSTR},
    Win32::{
        Foundation::{ERROR_NO_MORE_FILES, FILETIME, HANDLE},
        Storage::Packaging::Appx::GetApplicationUserModelId,
        System::{
            Com::{CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED},
            Diagnostics::ToolHelp::{
                CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
                TH32CS_SNAPPROCESS,
            },
            RemoteDesktop::ProcessIdToSessionId,
            RestartManager::{
                RmEndSession, RmRegisterResources, RmShutdown, RmStartSession, CCH_RM_SESSION_KEY,
                RM_UNIQUE_PROCESS,
            },
            Threading::{
                GetProcessTimes, OpenProcess, QueryFullProcessImageNameW, CREATE_NO_WINDOW,
                PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
            },
        },
        UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL},
    },
};

struct Process {
    client: Client,
    pid: u32,
    parent: u32,
    path: PathBuf,
    handle: Owned<HANDLE>,
}

fn client_for(name: &str) -> Option<Client> {
    match name.to_ascii_lowercase().as_str() {
        "code.exe" => Some(Client::VSCode),
        "chatgpt.exe" => Some(Client::ChatGPT),
        "claude.exe" => Some(Client::Claude),
        _ => None,
    }
}

fn desktop_install(client: Client, dir: &Path) -> bool {
    match client {
        // Both products also distribute a standalone CLI with the same executable name.
        Client::VSCode => {
            dir.join("bin/code.cmd").is_file() || dir.join("resources/app/product.json").is_file()
        }
        Client::Claude => dir.join("resources/app.asar").is_file(),
        Client::ChatGPT => true,
    }
}

fn identify(entry: &PROCESSENTRY32W, session: u32) -> Option<Process> {
    let end = entry.szExeFile.iter().position(|&c| c == 0)?;
    let client = client_for(&String::from_utf16_lossy(&entry.szExeFile[..end]))?;
    let pid = entry.th32ProcessID;
    let mut process_session = 0;
    unsafe { ProcessIdToSessionId(pid, &mut process_session) }.ok()?;
    if process_session != session {
        return None;
    }
    let handle =
        unsafe { Owned::new(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?) };
    let mut image = vec![0u16; 32768];
    let mut length = image.len() as u32;
    unsafe {
        QueryFullProcessImageNameW(
            *handle,
            PROCESS_NAME_WIN32,
            PWSTR(image.as_mut_ptr()),
            &mut length,
        )
    }
    .ok()?;
    let path = PathBuf::from(OsString::from_wide(&image[..length as usize]));
    // Recheck the image on the open handle in case the snapshot PID was reused.
    if client_for(path.file_name()?.to_str()?) != Some(client)
        || !desktop_install(client, path.parent()?)
    {
        return None;
    }
    Some(Process {
        client,
        pid,
        parent: entry.th32ParentProcessID,
        handle,
        path,
    })
}

fn processes() -> Result<Vec<Process>> {
    let error = |_| "无法读取本机应用进程。";
    let mut session = 0;
    let mut found = Vec::new();
    unsafe {
        ProcessIdToSessionId(std::process::id(), &mut session).map_err(error)?;
        let snapshot = Owned::new(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0).map_err(error)?);
        let mut entry = PROCESSENTRY32W {
            dwSize: size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        Process32FirstW(*snapshot, &mut entry).map_err(error)?;
        loop {
            if let Some(process) = identify(&entry, session) {
                found.push(process);
            }
            match Process32NextW(*snapshot, &mut entry) {
                Ok(()) => (),
                Err(e) if e.code() == ERROR_NO_MORE_FILES.to_hresult() => break,
                Err(_) => return Err("无法读取完整的应用进程列表。".into()),
            }
        }
    }
    Ok(found)
}

pub fn running() -> Result<Vec<Client>> {
    let mut result = Vec::new();
    for process in processes()? {
        if !result.contains(&process.client) {
            result.push(process.client);
        }
    }
    Ok(result)
}

fn packaged_id(handle: HANDLE) -> Option<String> {
    let mut id = [0u16; 256];
    let mut size = id.len() as u32;
    unsafe { GetApplicationUserModelId(handle, &mut size, Some(PWSTR(id.as_mut_ptr()))) }
        .ok()
        .ok()?;
    String::from_utf16(id.get(..size.checked_sub(1)? as usize)?).ok()
}

fn start_menu_id(client: Client) -> Result<String> {
    let name = match client {
        Client::VSCode => "Visual Studio Code",
        _ => client.name(),
    };
    let script = format!(
        "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); \
         (Get-StartApps | Where-Object Name -eq '{name}' | Select-Object -First 1).AppID"
    );
    let output = Command::new("powershell.exe")
        .args([
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            &script,
        ])
        .creation_flags(CREATE_NO_WINDOW.0)
        .output()
        .map_err(|_| "无法读取 Windows 开始菜单。")?;
    if !output.status.success() {
        return Err("无法读取 Windows 开始菜单。".into());
    }
    let id = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    if id.is_empty() {
        return Err(format!(
            "未找到 {}，请检查桌面应用是否已安装。",
            client.name()
        ));
    }
    Ok(id)
}

fn close(processes: &[&Process], client: Client) -> Result<()> {
    let mut apps = Vec::new();
    for process in processes {
        let mut times = [FILETIME::default(); 4];
        let [created, exited, kernel, user] = &mut times;
        unsafe { GetProcessTimes(*process.handle, created, exited, kernel, user) }
            .map_err(|_| "无法确认应用进程。")?;
        apps.push(RM_UNIQUE_PROCESS {
            dwProcessId: process.pid,
            ProcessStartTime: *created,
        });
    }
    let mut session = 0;
    let mut key = [0u16; CCH_RM_SESSION_KEY as usize + 1];
    unsafe {
        RmStartSession(&mut session, None, PWSTR(key.as_mut_ptr()))
            .ok()
            .map_err(|_| "无法创建 Windows 重启管理器会话。")?;
        let result = RmRegisterResources(session, None, Some(&apps), None)
            .ok()
            .and_then(|_| RmShutdown(session, 0, None).ok());
        let _ = RmEndSession(session);
        result
            .map_err(|_| format!("{} 未能正常退出，请处理应用中的提示后重试。", client.name()))?;
    }
    wait_for(client, false)
}

pub fn launch(client: Client, restart: bool) -> Result<()> {
    let mut instances = processes()?;
    instances.retain(|p| p.client == client);
    // Electron renderers and utility processes exit with their owning desktop process.
    let roots: Vec<_> = instances
        .iter()
        .filter(|p| !instances.iter().any(|other| other.pid == p.parent))
        .collect();
    let target = match roots.first() {
        Some(process) => match packaged_id(*process.handle) {
            Some(id) => HSTRING::from(format!(r"shell:AppsFolder\{id}")),
            None => HSTRING::from(process.path.as_os_str()),
        },
        None => HSTRING::from(format!(r"shell:AppsFolder\{}", start_menu_id(client)?)),
    };
    if restart && !roots.is_empty() {
        close(&roots, client)?;
    }
    let result = unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED)
            .ok()
            .map_err(|_| "无法初始化 Windows 应用启动服务。")?;
        let result = ShellExecuteW(None, PCWSTR::null(), &target, None, None, SW_SHOWNORMAL);
        CoUninitialize();
        result
    };
    if result.0 as isize <= 32 {
        return Err(format!("Windows 无法打开 {}。", client.name()));
    }
    Ok(())
}
