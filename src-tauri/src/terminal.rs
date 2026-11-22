use portable_pty::{native_pty_system, CommandBuilder, MasterPty, PtySize};
use serde::Serialize;
#[cfg(not(windows))]
use std::path::Path;
use std::{
    collections::HashMap,
    io::{Read, Write},
    path::PathBuf,
    sync::{Arc, Mutex},
    thread,
    time::Duration,
};
use tauri::{AppHandle, Emitter, State};
use uuid::Uuid;

type SessionId = String;

#[derive(Default)]
pub struct TerminalManager {
    sessions: Arc<Mutex<HashMap<SessionId, Arc<Mutex<TerminalSession>>>>>,
}

impl Drop for TerminalManager {
    fn drop(&mut self) {
        if let Ok(sessions) = self.sessions.lock() {
            for session in sessions.values() {
                if let Ok(mut session) = session.lock() {
                    let _ = session.child.kill();
                }
            }
        }
    }
}

struct TerminalSession {
    master: Box<dyn MasterPty + Send>,
    writer: Box<dyn Write + Send>,
    child: Box<dyn portable_pty::Child + Send + Sync>,
}

impl Drop for TerminalSession {
    fn drop(&mut self) {
        let _ = self.child.kill();
    }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NewTerminal {
    id: String,
    working_directory: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TerminalEvent {
    session_id: String,
    data: Vec<u8>,
}

fn default_directory() -> Result<PathBuf, String> {
    dirs::home_dir().ok_or_else(|| "无法确定用户主目录。".to_string())
}

fn shell_command() -> CommandBuilder {
    #[cfg(windows)]
    {
        let mut command = CommandBuilder::new("powershell.exe");
        command.arg("-NoLogo");
        command
    }
    #[cfg(not(windows))]
    {
        let shell = std::env::var_os("SHELL")
            .filter(|value| Path::new(value).is_file())
            .unwrap_or_else(|| "/bin/sh".into());
        let mut command = CommandBuilder::new(shell);
        command.arg("-l");
        command
    }
}

#[tauri::command]
pub fn open_terminal(
    app: AppHandle,
    manager: State<'_, TerminalManager>,
    working_directory: Option<String>,
    columns: Option<u16>,
    rows: Option<u16>,
) -> Result<NewTerminal, String> {
    let directory = match working_directory.filter(|value| !value.trim().is_empty()) {
        Some(path) => PathBuf::from(path),
        None => default_directory()?,
    };
    if !directory.is_dir() {
        return Err("终端工作目录不存在或不是文件夹。".into());
    }
    let directory = directory
        .canonicalize()
        .map_err(|_| "无法打开终端工作目录。".to_string())?;
    let size = PtySize {
        cols: columns.unwrap_or(100).clamp(20, 400),
        rows: rows.unwrap_or(28).clamp(5, 200),
        pixel_width: 0,
        pixel_height: 0,
    };
    let pair = native_pty_system()
        .openpty(size)
        .map_err(|_| "无法创建终端会话。".to_string())?;
    let mut command = shell_command();
    command.cwd(&directory);
    let child = pair
        .slave
        .spawn_command(command)
        .map_err(|_| "无法启动系统终端。".to_string())?;
    drop(pair.slave);
    let mut reader = pair
        .master
        .try_clone_reader()
        .map_err(|_| "无法读取终端输出。".to_string())?;
    let writer = pair
        .master
        .take_writer()
        .map_err(|_| "无法向终端发送输入。".to_string())?;
    let id = Uuid::new_v4().to_string();
    let session = Arc::new(Mutex::new(TerminalSession {
        master: pair.master,
        writer,
        child,
    }));
    manager
        .sessions
        .lock()
        .map_err(|_| "终端会话状态不可用。".to_string())?
        .insert(id.clone(), session.clone());

    let output_app = app.clone();
    let output_id = id.clone();
    thread::spawn(move || {
        let mut buffer = [0_u8; 4096];
        loop {
            match reader.read(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(length) => {
                    let event = TerminalEvent {
                        session_id: output_id.clone(),
                        data: buffer[..length].to_vec(),
                    };
                    if output_app.emit("terminal-output", event).is_err() {
                        break;
                    }
                }
            }
        }
    });

    let monitor_app = app;
    let sessions = Arc::clone(&manager.sessions);
    let monitor_manager = manager
        .inner()
        .sessions
        .lock()
        .map_err(|_| "终端会话状态不可用。")?;
    let monitor_session = monitor_manager
        .get(&id)
        .cloned()
        .ok_or("终端会话创建失败。")?;
    drop(monitor_manager);
    let monitor_id = id.clone();
    thread::spawn(move || loop {
        let status = match monitor_session.lock() {
            Ok(mut session) => session.child.try_wait().ok().flatten(),
            Err(_) => None,
        };
        if let Some(status) = status {
            if let Ok(mut sessions) = sessions.lock() {
                sessions.remove(&monitor_id);
            }
            let _ = monitor_app.emit(
                "terminal-exited",
                serde_json::json!({ "sessionId": monitor_id, "code": status.exit_code() }),
            );
            break;
        }
        thread::sleep(Duration::from_millis(400));
    });

    Ok(NewTerminal {
        id,
        working_directory: directory.to_string_lossy().into_owned(),
    })
}

#[tauri::command]
pub fn terminal_input(
    manager: State<'_, TerminalManager>,
    id: String,
    data: String,
) -> Result<(), String> {
    let session = manager
        .sessions
        .lock()
        .map_err(|_| "终端会话状态不可用。".to_string())?
        .get(&id)
        .cloned()
        .ok_or_else(|| "终端会话已关闭。".to_string())?;
    let mut session = session
        .lock()
        .map_err(|_| "终端会话状态不可用。".to_string())?;
    session
        .writer
        .write_all(data.as_bytes())
        .map_err(|_| "终端输入发送失败。".to_string())?;
    session
        .writer
        .flush()
        .map_err(|_| "终端输入发送失败。".to_string())
}

#[tauri::command]
pub fn terminal_resize(
    manager: State<'_, TerminalManager>,
    id: String,
    columns: u16,
    rows: u16,
) -> Result<(), String> {
    let session = manager
        .sessions
        .lock()
        .map_err(|_| "终端会话状态不可用。".to_string())?
        .get(&id)
        .cloned()
        .ok_or_else(|| "终端会话已关闭。".to_string())?;
    let result = session
        .lock()
        .map_err(|_| "终端会话状态不可用。".to_string())?
        .master
        .resize(PtySize {
            cols: columns.clamp(20, 400),
            rows: rows.clamp(5, 200),
            pixel_width: 0,
            pixel_height: 0,
        })
        .map_err(|_| "终端尺寸调整失败。".to_string());
    result
}

#[tauri::command]
pub fn close_terminal(manager: State<'_, TerminalManager>, id: String) -> Result<(), String> {
    let session = manager
        .sessions
        .lock()
        .map_err(|_| "终端会话状态不可用。".to_string())?
        .remove(&id);
    if let Some(session) = session {
        if let Ok(mut session) = session.lock() {
            let _ = session.child.kill();
        }
    }
    Ok(())
}
