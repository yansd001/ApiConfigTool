import { useEffect, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { ChevronRight, Download, ExternalLink, FolderOpen, Loader2 } from "lucide-react";
import { api, desktop, errorText } from "@/lib/api";
import { Button } from "./ui/button";

export function WorkspacePage({ active }: { active: boolean }) {
  const [installed, setInstalled] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [projects, setProjects] = useState<string[]>([]);
  const [historyError, setHistoryError] = useState("");
  const historyRequest = useRef(0);

  useEffect(() => {
    if (!desktop || !installed || !active) return;
    function refresh() {
      const request = ++historyRequest.current;
      api.recentWorkspaceProjects().then((paths) => {
        if (request !== historyRequest.current) return;
        setProjects(paths);
        setHistoryError("");
      }).catch((reason) => {
        if (request === historyRequest.current) setHistoryError(errorText(reason));
      });
    }
    refresh();
    window.addEventListener("focus", refresh);
    return () => {
      historyRequest.current++;
      window.removeEventListener("focus", refresh);
    };
  }, [active, installed]);

  useEffect(() => {
    if (desktop)
      api
        .workspaceStatus()
        .then(setInstalled)
        .catch((reason) => setError(errorText(reason)));
  }, []);

  async function install() {
    setBusy(true);
    setMessage("准备安装…");
    setError("");
    try {
      await api.installWorkspace(setMessage);
      setInstalled(true);
      setMessage("安装完成，可以打开编辑器。");
    } catch (reason) {
      setMessage("");
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  }

  async function launch(project?: string | null) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      if (project === null) {
        const folder = await open({
          directory: true,
          multiple: false,
          title: "选择项目文件夹",
        });
        if (!folder) return;
        project = folder;
      }
      await api.openWorkspace(project);
      if (project) {
        const opened = project;
        const key = (path: string) => /^[a-z]:[\\/]|^\\\\/i.test(path) ? path.toLowerCase() : path;
        historyRequest.current++;
        setProjects((previous) => [opened, ...previous.filter((path) => key(path) !== key(opened))]);
      }
      setMessage("已打开内置 VS Code。");
    } catch (reason) {
      setError(errorText(reason));
      try {
        setInstalled(await api.workspaceStatus());
      } catch {
        /* Keep the launch error visible. */
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="tool-page workspace-page">
      <div className="tool-page-header">
        <div>
          <h1>内置 VS Code</h1>
          <p>可选安装微软 Visual Studio Code，使用独立的扩展和设置目录。</p>
        </div>
      </div>
      <section className="workspace-install" aria-label="编辑器安装">
        <div>
          <h2>Visual Studio Code</h2>
          <p>包含 Codex、Claude Code、Git 版本控制和简体中文语言包。</p>
          <p>
            {installed
              ? "已安装 · 默认使用简体中文"
              : "首次安装约需下载 1 GB。"}
          </p>
        </div>
        <div className="workspace-actions">
          {installed ? (
            <>
              <Button disabled={busy} onClick={() => void launch()}>
                <ExternalLink size={15} />
                打开 VS Code
              </Button>
              <Button
                variant="neutral"
                disabled={busy}
                onClick={() => void launch(null)}
              >
                <FolderOpen size={15} />
                打开项目
              </Button>
            </>
          ) : (
            <Button
              disabled={!desktop || installed === null || busy}
              onClick={() => void install()}
            >
              {busy ? (
                <Loader2 className="animate-spin" size={15} />
              ) : (
                <Download size={15} />
              )}
              {busy ? "正在安装" : "安装 VS Code"}
            </Button>
          )}
        </div>
      </section>
      {!desktop && (
        <div className="preview-notice">请在桌面应用中安装编辑器。</div>
      )}
      {message && (
        <div className="success-message" role="status">
          {message}
        </div>
      )}
      {error && (
        <div className="error-message page-error" role="alert">
          {error}
        </div>
      )}
      {installed && (
        <section className="workspace-history" aria-labelledby="recent-projects">
          <h2 id="recent-projects">最近打开的项目</h2>
          {historyError && <p className="error-message" role="alert">{historyError}</p>}
          {projects.length > 0 ? (
            <ul>
              {projects.map((path) => (
                <li key={path}>
                  <button type="button" disabled={busy} title={path} onClick={() => void launch(path)}>
                    <FolderOpen size={17} />
                    <span><strong>{path.split(/[\\/]/).filter(Boolean).pop() || path}</strong><small>{path}</small></span>
                    <ChevronRight size={15} />
                  </button>
                </li>
              ))}
            </ul>
          ) : !historyError && <p>暂无最近打开的项目</p>}
        </section>
      )}
    </div>
  );
}
