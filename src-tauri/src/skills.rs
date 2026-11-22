use crate::store::Paths;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Component, Path, PathBuf},
    process::Command,
};
use tauri::{command, State};

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillSearchResult {
    pub id: String,
    pub source: String,
    pub skill_id: String,
    pub name: String,
    pub installs: u64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledSkill {
    pub name: String,
    pub app: String,
    pub path: String,
}

fn skill_directory(paths: &Paths, app: &str) -> Result<PathBuf, String> {
    match app {
        "codex" => Ok(paths.codex.join("skills")),
        "claude" => Ok(paths.claude.join("skills")),
        _ => Err("请选择 Codex 或 Claude Code。".into()),
    }
}

#[command]
pub fn list_skills(paths: State<'_, Paths>) -> Result<Vec<InstalledSkill>, String> {
    let mut result = Vec::new();
    for app in ["codex", "claude"] {
        let directory = skill_directory(&paths, app)?;
        let Ok(entries) = fs::read_dir(&directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() && path.join("SKILL.md").is_file() {
                result.push(InstalledSkill {
                    name: entry.file_name().to_string_lossy().into_owned(),
                    app: app.to_owned(),
                    path: path.to_string_lossy().into_owned(),
                });
            }
        }
    }
    result.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
    Ok(result)
}

#[derive(Deserialize)]
struct SearchResponse {
    skills: Vec<SkillSearchResult>,
}

#[command]
pub async fn search_skills(query: String) -> Result<Vec<SkillSearchResult>, String> {
    let query = query.trim();
    if query.len() < 2 || query.len() > 100 {
        return Err("搜索词长度需要在 2 到 100 个字符之间。".into());
    }
    let response = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .map_err(|_| "无法创建 Skills 搜索请求。".to_string())?
        .get("https://skills.sh/api/search")
        .query(&[("q", query)])
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|_| "Skills 搜索服务暂时不可用。".to_string())?;
    if !response.status().is_success() {
        return Err(format!(
            "Skills 搜索返回 HTTP {}。",
            response.status().as_u16()
        ));
    }
    let bytes = response
        .bytes()
        .await
        .map_err(|_| "读取 Skills 搜索结果失败。".to_string())?;
    if bytes.len() > 2 * 1024 * 1024 {
        return Err("Skills 搜索结果超过 2 MB。".into());
    }
    let mut results: SearchResponse = serde_json::from_slice(&bytes)
        .map_err(|_| "Skills 搜索服务返回了无效数据。".to_string())?;
    results.skills.truncate(50);
    Ok(results.skills)
}

fn valid_repo_source(source: &str) -> bool {
    let parts: Vec<_> = source.split('/').collect();
    parts.len() == 2
        && parts.iter().all(|part| {
            !part.is_empty()
                && part
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || "-_.".contains(c))
        })
}

fn valid_relative_path(value: &str) -> bool {
    !value.is_empty()
        && Path::new(value)
            .components()
            .all(|component| matches!(component, Component::Normal(_)))
}

fn locate_skill(repository: &Path, skill_id: &str, display_name: &str) -> Result<PathBuf, String> {
    let expected_id = skill_id
        .rsplit('/')
        .next()
        .unwrap_or(skill_id)
        .to_lowercase();
    let expected_name = display_name.to_lowercase();
    let mut pending = vec![repository.to_path_buf()];
    let mut visited = 0;
    while let Some(directory) = pending.pop() {
        visited += 1;
        if visited > 20_000 {
            return Err("仓库目录数量过多，无法安全扫描 Skill。".into());
        }
        let Ok(entries) = fs::read_dir(&directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let Ok(kind) = entry.file_type() else {
                continue;
            };
            if !kind.is_dir() {
                continue;
            }
            if path.join("SKILL.md").is_file() {
                let folder = entry.file_name().to_string_lossy().to_lowercase();
                let frontmatter = fs::read_to_string(path.join("SKILL.md")).unwrap_or_default();
                let declared_name = frontmatter
                    .lines()
                    .take(24)
                    .find_map(|line| line.trim().strip_prefix("name:"))
                    .map(|name| name.trim().trim_matches(['"', '\'']).to_lowercase());
                if folder == expected_id
                    || declared_name.as_deref() == Some(expected_id.as_str())
                    || declared_name.as_deref() == Some(expected_name.as_str())
                    || folder == expected_name.replace(' ', "-")
                {
                    return Ok(path);
                }
            }
            if entry.file_name() != ".git" {
                pending.push(path);
            }
        }
    }
    Err("仓库中未找到该 Skill 的 SKILL.md。".into())
}

fn install_from_github(
    paths: &Paths,
    source: &str,
    skill_id: &str,
    name: &str,
    app: &str,
) -> Result<InstalledSkill, String> {
    if !valid_repo_source(source) || !valid_relative_path(skill_id) {
        return Err("Skills 来源信息无效。".into());
    }
    let destination_root = skill_directory(paths, app)?;
    let folder_name = skill_id
        .rsplit('/')
        .next()
        .filter(|value| !value.is_empty())
        .ok_or_else(|| "Skill 名称无效。".to_string())?;
    let folder_name: String = folder_name
        .chars()
        .map(|value| {
            if value.is_ascii_alphanumeric() || "-_".contains(value) {
                value
            } else {
                '-'
            }
        })
        .collect();
    if folder_name.is_empty() || folder_name == "." || folder_name == ".." {
        return Err("Skill 名称无效。".into());
    }
    let destination = destination_root.join(&folder_name);
    if destination.exists() {
        return Err(format!("「{name}」已经安装在 {app} 中。"));
    }
    let temporary = tempfile::tempdir().map_err(|_| "无法创建 Skills 临时目录。".to_string())?;
    let repository = temporary.path().join("repository");
    let output = Command::new("git")
        .args([
            "clone",
            "--depth",
            "1",
            &format!("https://github.com/{source}.git"),
        ])
        .arg(&repository)
        .output()
        .map_err(|_| "安装 Skills 需要先安装 Git。".to_string())?;
    if !output.status.success() {
        return Err("无法下载该 Skill 仓库，请检查网络或仓库是否公开。".into());
    }
    let skill_path = locate_skill(&repository, skill_id, name)?;
    fs::create_dir_all(&destination_root).map_err(|_| "无法创建 Skills 目录。".to_string())?;
    fs::create_dir(&destination).map_err(|_| "无法创建 Skill 安装目录。".to_string())?;
    if let Err(error) = copy_directory(&skill_path, &destination) {
        let _ = fs::remove_dir_all(&destination);
        return Err(error);
    }
    Ok(InstalledSkill {
        name: folder_name,
        app: app.to_owned(),
        path: destination.to_string_lossy().into_owned(),
    })
}

fn copy_directory(source: &Path, destination: &Path) -> Result<(), String> {
    for entry in fs::read_dir(source).map_err(|_| "读取 Skill 文件失败。".to_string())? {
        let entry = entry.map_err(|_| "读取 Skill 文件失败。".to_string())?;
        let file_type = entry
            .file_type()
            .map_err(|_| "读取 Skill 文件属性失败。".to_string())?;
        let target = destination.join(entry.file_name());
        if file_type.is_dir() {
            fs::create_dir(&target).map_err(|_| "复制 Skill 文件失败。".to_string())?;
            copy_directory(&entry.path(), &target)?;
        } else if file_type.is_file() {
            fs::copy(entry.path(), target).map_err(|_| "复制 Skill 文件失败。".to_string())?;
        }
    }
    Ok(())
}

#[command]
pub async fn install_skill(
    paths: State<'_, Paths>,
    source: String,
    skill_id: String,
    name: String,
    app: String,
) -> Result<InstalledSkill, String> {
    let paths = paths.inner().clone();
    tokio::task::spawn_blocking(move || {
        install_from_github(&paths, &source, &skill_id, &name, &app)
    })
    .await
    .map_err(|_| "Skill 安装任务失败。".to_string())?
}

#[command]
pub fn remove_skill(paths: State<'_, Paths>, app: String, name: String) -> Result<(), String> {
    if !valid_relative_path(&name) || name.contains(['/', '\\']) {
        return Err("Skill 名称无效。".into());
    }
    let path = skill_directory(&paths, &app)?.join(name);
    if !path.is_dir() || !path.join("SKILL.md").is_file() {
        return Err("找不到已安装的 Skill。".into());
    }
    fs::remove_dir_all(path).map_err(|_| "卸载 Skill 失败。".into())
}
