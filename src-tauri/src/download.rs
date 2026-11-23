use crate::files::Result;
use sha2::{Digest, Sha256};
use std::{
    fs::File,
    io::Write,
    path::Path,
    time::{Duration, Instant},
};
use tauri::ipc::Channel;

pub(crate) async fn download(
    url: &str,
    sha256: &str,
    path: &Path,
    name: &str,
    progress: &Channel<String>,
) -> Result<()> {
    let mut response = reqwest::Client::builder()
        .connect_timeout(Duration::from_secs(20))
        .read_timeout(Duration::from_secs(90))
        .build()
        .map_err(|e| e.to_string())?
        .get(url)
        .header("User-Agent", "ApiConfigTool")
        .send()
        .await
        .and_then(reqwest::Response::error_for_status)
        .map_err(|e| format!("下载 {name} 失败：{e}"))?;
    let total = response.content_length();
    let mut out = File::create(path).map_err(|e| format!("写入 {name} 失败：{e}"))?;
    let mut digest = Sha256::new();
    let mut size = 0u64;
    let mut last = Instant::now();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| format!("下载 {name} 中断，请重试。"))?
    {
        size += chunk.len() as u64;
        if size > 1024 * 1024 * 1024 {
            return Err(format!("{name} 安装包大小异常。"));
        }
        out.write_all(&chunk)
            .map_err(|e| format!("写入 {name} 失败：{e}"))?;
        digest.update(chunk);
        if last.elapsed() >= Duration::from_millis(400) {
            let amount = total
                .map(|n| format!(" / {} MiB", n.div_ceil(1048576)))
                .unwrap_or_default();
            let _ = progress.send(format!("正在下载 {name}，{} MiB{amount}", size / 1048576));
            last = Instant::now();
        }
    }
    out.sync_all()
        .map_err(|e| format!("写入 {name} 失败：{e}"))?;
    if format!("{:x}", digest.finalize()) != sha256 {
        return Err(format!("{name} 完整性校验失败，请重新下载。"));
    }
    Ok(())
}
