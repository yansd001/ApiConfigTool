use fs2::FileExt;
use serde_json::Value;
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
};

pub type Result<T> = std::result::Result<T, String>;

pub fn read(path: &Path) -> Result<Option<Vec<u8>>> {
    match fs::read(path) {
        Ok(bytes) => Ok(Some(bytes)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(_) => Err(format!("无法读取 {}，请检查文件权限。", path.display())),
    }
}

pub fn text(bytes: &Option<Vec<u8>>) -> Result<&str> {
    std::str::from_utf8(bytes.as_deref().unwrap_or_default())
        .map(|s| s.trim_start_matches('\u{feff}'))
        .map_err(|_| "配置文件不是有效的 UTF-8 文本。".into())
}

// Reject duplicate JSON keys at every nesting level instead of silently accepting
// a different credential from the one displayed in the editor.
pub fn json(input: &str) -> Result<Value> {
    use serde::de::{Deserialize, Deserializer, Error, MapAccess, SeqAccess, Visitor};
    struct Strict(Value);
    impl<'de> Deserialize<'de> for Strict {
        fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
            struct V;
            impl<'de> Visitor<'de> for V {
                type Value = Strict;
                fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
                    f.write_str("JSON value")
                }
                fn visit_map<M: MapAccess<'de>>(
                    self,
                    mut m: M,
                ) -> std::result::Result<Strict, M::Error> {
                    let mut out = serde_json::Map::new();
                    while let Some((k, Strict(v))) = m.next_entry::<String, Strict>()? {
                        if out.contains_key(&k) {
                            return Err(M::Error::custom("duplicate key"));
                        }
                        out.insert(k, v);
                    }
                    Ok(Strict(Value::Object(out)))
                }
                fn visit_seq<S: SeqAccess<'de>>(
                    self,
                    mut s: S,
                ) -> std::result::Result<Strict, S::Error> {
                    let mut out = Vec::new();
                    while let Some(Strict(v)) = s.next_element()? {
                        out.push(v);
                    }
                    Ok(Strict(Value::Array(out)))
                }
                fn visit_str<E: Error>(self, v: &str) -> std::result::Result<Strict, E> {
                    Ok(Strict(v.into()))
                }
                fn visit_bool<E: Error>(self, v: bool) -> std::result::Result<Strict, E> {
                    Ok(Strict(v.into()))
                }
                fn visit_i64<E: Error>(self, v: i64) -> std::result::Result<Strict, E> {
                    Ok(Strict(v.into()))
                }
                fn visit_u64<E: Error>(self, v: u64) -> std::result::Result<Strict, E> {
                    Ok(Strict(v.into()))
                }
                fn visit_f64<E: Error>(self, v: f64) -> std::result::Result<Strict, E> {
                    Ok(Strict(serde_json::json!(v)))
                }
                fn visit_unit<E: Error>(self) -> std::result::Result<Strict, E> {
                    Ok(Strict(Value::Null))
                }
            }
            d.deserialize_any(V)
        }
    }
    let mut de = serde_json::Deserializer::from_str(input.trim_start_matches('\u{feff}'));
    let Strict(value) = Strict::deserialize(&mut de).map_err(|e| {
        format!(
            "JSON 无效或含重复字段（第 {} 行，第 {} 列）。",
            e.line(),
            e.column()
        )
    })?;
    de.end()
        .map_err(|_| "JSON 末尾存在多余内容。".to_string())?;
    if !value.is_object() {
        return Err("配置必须是 JSON 对象。".into());
    }
    Ok(value)
}

pub fn json_or_empty(bytes: &Option<Vec<u8>>) -> Result<Value> {
    match bytes {
        None => Ok(serde_json::json!({})),
        Some(_) => json(text(bytes)?),
    }
}
pub fn encode(value: &Value) -> Vec<u8> {
    let mut bytes = serde_json::to_vec_pretty(value).expect("JSON serialization");
    bytes.push(b'\n');
    bytes
}

pub struct Edit {
    pub path: PathBuf,
    pub before: Option<Vec<u8>>,
    pub after: Vec<u8>,
}
impl Edit {
    pub fn load(path: PathBuf) -> Result<Self> {
        Ok(Self {
            before: read(&path)?,
            path,
            after: Vec::new(),
        })
    }
}

pub fn lock(directory: &Path) -> Result<File> {
    fs::create_dir_all(directory).map_err(|_| "无法创建配置库目录。".to_string())?;
    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(directory.join(".lock"))
        .map_err(|_| "无法打开配置库锁。".to_string())?;
    file.try_lock_exclusive()
        .map_err(|_| "另一个操作正在修改配置，请稍后重试。".to_string())?;
    Ok(file)
}

pub fn replace(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path.parent().ok_or("无效的配置路径。")?;
    fs::create_dir_all(parent).map_err(|_| format!("无法创建目录 {}。", parent.display()))?;
    let mut temp = tempfile::NamedTempFile::new_in(parent)
        .map_err(|_| "无法创建临时配置文件。".to_string())?;
    temp.write_all(bytes)
        .and_then(|_| temp.as_file().sync_all())
        .map_err(|_| "临时配置写入失败。".to_string())?;
    temp.persist(path)
        .map_err(|_| format!("无法替换 {}，请检查文件权限或占用。", path.display()))?;
    Ok(())
}

pub fn commit(edits: &[Edit], backups: &Path) -> Result<()> {
    commit_using(edits, backups, replace)
}

pub(crate) fn commit_using(
    edits: &[Edit],
    backups: &Path,
    writer: impl Fn(&Path, &[u8]) -> Result<()>,
) -> Result<()> {
    // Check every file before the first write, then re-check immediately before each replacement.
    for edit in edits {
        if read(&edit.path)? != edit.before {
            return Err("配置被其他程序修改，请刷新后重试。".into());
        }
    }
    let changed: Vec<_> = edits
        .iter()
        .filter(|e| e.before.as_deref() != Some(&e.after))
        .collect();
    if changed.is_empty() {
        return Ok(());
    }
    let directory = backups.join(uuid::Uuid::new_v4().to_string());
    let mut manifest = Vec::new();
    for (i, edit) in changed.iter().enumerate() {
        if let Some(before) = &edit.before {
            replace(&directory.join(format!("{i}.bak")), before)?;
        }
        manifest.push(serde_json::json!({ "path": edit.path, "backup": edit.before.as_ref().map(|_| format!("{i}.bak")) }));
    }
    replace(
        &directory.join("manifest.json"),
        &encode(&serde_json::json!({ "files": manifest })),
    )?;
    for (index, edit) in changed.iter().enumerate() {
        let result = match read(&edit.path) {
            Ok(before) if before == edit.before => writer(&edit.path, &edit.after),
            _ => Err("配置被其他程序修改，请刷新后重试。".into()),
        };
        if let Err(error) = result {
            let mut rollback_failed = false;
            for prior in changed[..index].iter().rev() {
                // Do not overwrite an external edit made after our replacement.
                if read(&prior.path).ok().flatten().as_deref() != Some(&prior.after) {
                    rollback_failed = true;
                    continue;
                }
                let result = match &prior.before {
                    Some(bytes) => replace(&prior.path, bytes),
                    None => fs::remove_file(&prior.path)
                        .map_err(|_| "无法删除临时创建的配置。".to_string()),
                };
                rollback_failed |= result.is_err();
            }
            return Err(if rollback_failed {
                format!(
                    "{error} 部分文件未能恢复；备份位于 {}。",
                    directory.display()
                )
            } else {
                format!("{error} 已恢复此前写入的文件。")
            });
        }
    }
    Ok(())
}
