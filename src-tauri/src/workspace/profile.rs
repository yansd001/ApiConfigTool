use super::Result;
use rusqlite::{Connection, OpenFlags, OptionalExtension, TransactionBehavior};
use serde_json::{json, Value};
use std::{
    collections::HashSet,
    fs,
    path::{Path, PathBuf},
};

const CODEX: &str = "workbench.view.extension.codexSecondaryViewContainer";
const CLAUDE: &str = "workbench.view.extension.claude-sidebar-secondary";
const PINNED: &str = "workbench.auxiliarybar.pinnedPanels";

fn storage_error(error: rusqlite::Error) -> String {
    format!("无法访问内置 VS Code 状态库：{error}")
}

fn read_item(db: &Connection, key: &str) -> Result<Option<String>> {
    db.query_row(
        "SELECT CAST(value AS TEXT) FROM ItemTable WHERE key = ?1",
        [key],
        |row| row.get(0),
    )
    .optional()
    .map_err(storage_error)
}

pub(super) fn configure_panels(root: &Path) -> Result<()> {
    let dir = root.join("user-data/User/globalStorage");
    fs::create_dir_all(&dir).map_err(super::io_error)?;
    let mut db = Connection::open(dir.join("state.vscdb")).map_err(storage_error)?;
    let tx = db
        .transaction_with_behavior(TransactionBehavior::Immediate)
        .map_err(storage_error)?;
    // VS Code 1.147 restores the secondary sidebar's tabs in their stored array order.
    tx.execute_batch(
        "CREATE TABLE IF NOT EXISTS ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)",
    )
    .map_err(storage_error)?;
    let stored = read_item(&tx, PINNED)?;
    let mut panels: Vec<Value> = serde_json::from_str(stored.as_deref().unwrap_or("[]"))
        .map_err(|e| format!("无法读取内置 VS Code 侧栏布局：{e}"))?;
    let mut positions = Vec::new();
    for id in [CODEX, CLAUDE] {
        let index = panels
            .iter()
            .position(|panel| panel["id"] == id)
            .unwrap_or_else(|| {
                panels.push(json!({ "id": id, "pinned": true, "visible": false }));
                panels.len() - 1
            });
        panels[index]["pinned"] = true.into();
        positions.push(index);
    }
    if positions[0] > positions[1] {
        panels.swap(positions[0], positions[1]);
    }
    let updated = serde_json::to_string(&panels).map_err(|e| e.to_string())?;
    if stored.as_ref() != Some(&updated) {
        tx.execute(
            "INSERT OR REPLACE INTO ItemTable (key, value) VALUES (?1, ?2)",
            [PINNED, &updated],
        )
        .map_err(storage_error)?;
    }
    tx.commit().map_err(storage_error)
}

pub(super) fn is_workspace_file(path: &Path) -> bool {
    path.extension()
        .is_some_and(|ext| ext.eq_ignore_ascii_case("code-workspace"))
}

pub(super) fn recent_projects(root: &Path) -> Result<Vec<PathBuf>> {
    // The portable runtime keeps its native Open Recent list in application-shared storage.
    let path = root.join("shared-data/sharedStorage/state.vscdb");
    if !path.try_exists().map_err(super::io_error)? {
        return Ok(Vec::new());
    }
    let db = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)
        .map_err(storage_error)?;
    let Some(raw) = read_item(&db, "history.recentlyOpenedPathsList")? else {
        return Ok(Vec::new());
    };
    #[derive(serde::Deserialize)]
    struct History {
        entries: Vec<Value>,
    }
    let history: History =
        serde_json::from_str(&raw).map_err(|e| format!("无法读取最近项目：{e}"))?;
    let mut projects = Vec::new();
    let mut seen = HashSet::new();
    for entry in history.entries {
        if entry["remoteAuthority"]
            .as_str()
            .is_some_and(|s| !s.is_empty())
        {
            continue;
        }
        let folder = entry["folderUri"].as_str();
        let uri = folder.or_else(|| entry["workspace"]["configPath"].as_str());
        let Some(path) = uri
            .and_then(|s| url::Url::parse(s).ok())
            .filter(|u| u.scheme() == "file")
            .and_then(|u| u.to_file_path().ok())
        else {
            continue;
        };
        if folder.is_none() && !is_workspace_file(&path) {
            continue;
        }
        let mut key = path.to_string_lossy().into_owned();
        if cfg!(windows) {
            key = key.to_lowercase();
        }
        if seen.insert(key) {
            projects.push(path);
        }
    }
    Ok(projects)
}
