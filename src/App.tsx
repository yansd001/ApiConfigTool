import { lazy, Suspense, useCallback, useEffect, useRef, useState } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";
import {
  Check,
  CheckCheck,
  Copy,
  Download,
  ExternalLink,
  FileJson2,
  Github,
  Globe2,
  Info,
  Loader2,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Server,
  Trash2,
  UserRound,
  Wallet,
  X,
} from "lucide-react";
import {
  api,
  apps,
  appNames,
  desktop,
  emptyProfile,
  errorText,
  visibleProfiles,
  type AppId,
  type Profile,
  type Snapshot,
} from "./lib/api";
import { Sidebar } from "./components/shell/Sidebar";
import { AppPageHeader } from "./components/shell/AppPageHeader";
import { ClientActions } from "./components/shell/ClientActions";
import { Button } from "./components/ui/button";
import { Input } from "./components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "./components/ui/dialog";
import { ProviderEditor } from "./components/ProviderEditor";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { BalanceDisplay } from "./components/BalanceDisplay";
import { SkillsPage } from "./components/SkillsPage";

const TerminalPage = lazy(() =>
  import("./components/TerminalPage").then((module) => ({
    default: module.TerminalPage,
  })),
);

const projectUrl = "https://github.com/yansd001/ApiConfigTool";

export default function App() {
  const [app, setApp] = useState<AppId>(() => {
    const saved = localStorage.getItem("selected-app");
    return apps.includes(saved as AppId) ? (saved as AppId) : "codex";
  });
  const [section, setSection] = useState<"providers" | "terminal" | "skills">("providers");
  const [terminalVisited, setTerminalVisited] = useState(false);
  const [skillsVisited, setSkillsVisited] = useState(false);
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState("active");
  const [editor, setEditor] = useState<Profile | null>(null);
  const [editorTab, setEditorTab] = useState<"basic" | "balance">("basic");
  const [deleting, setDeleting] = useState<Profile | null>(null);
  const [error, setError] = useState("");
  const [clientError, setClientError] = useState("");
  const [toast, setToast] = useState("");
  const [about, setAbout] = useState(false);
  const [projectLinkError, setProjectLinkError] = useState("");
  const [collapsed, setCollapsed] = useState(false);
  const [dark, setDark] = useState(
    () => localStorage.getItem("theme") === "dark",
  );
  const [exitPending, setExitPending] = useState(false);
  const dirty = useRef(false);
  const mutation = useRef(false);
  const generation = useRef(0);
  const onDirty = useCallback((value: boolean) => {
    dirty.current = value;
  }, []);

  const onEditorBusy = useCallback((value: boolean) => {
    mutation.current = value;
    if (value) generation.current++;
  }, []);

  const navigate = useCallback((next: "providers" | "terminal" | "skills") => {
    setSection(next);
    if (next === "terminal") setTerminalVisited(true);
    if (next === "skills") setSkillsVisited(true);
  }, []);

  useEffect(() => {
    document.documentElement.classList.toggle("dark", dark);
    localStorage.setItem("theme", dark ? "dark" : "light");
  }, [dark]);
  useEffect(() => {
    if (!toast) return;
    const timeout = setTimeout(() => setToast(""), 5500);
    return () => clearTimeout(timeout);
  }, [toast]);
  const refresh = useCallback(
    async (silent = false) => {
      const current = ++generation.current;
      if (!silent) setLoading(true);
      try {
        const result = await api.list(app);
        if (current === generation.current) {
          setSnapshot(result);
          setError("");
        }
      } catch (e) {
        if (current === generation.current) setError(errorText(e));
      } finally {
        if (current === generation.current) setLoading(false);
      }
    },
    [app],
  );
  useEffect(() => {
    setSnapshot(null);
    setSearch("");
    setError("");
    localStorage.setItem("selected-app", app);
    void refresh();
    return () => {
      generation.current++;
    };
  }, [app, refresh]);
  useEffect(() => {
    const focused = () => {
      if (!mutation.current) void refresh(true);
    };
    window.addEventListener("focus", focused);
    return () => window.removeEventListener("focus", focused);
  }, [refresh]);
  useEffect(() => {
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (dirty.current) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    window.addEventListener("beforeunload", beforeUnload);
    let dispose: (() => void) | undefined;
    let stopped = false;
    if (desktop)
      void import("@tauri-apps/api/window")
        .then(async ({ getCurrentWindow }) => {
          const unlisten = await getCurrentWindow().onCloseRequested(
            (event) => {
              if (mutation.current) {
                event.preventDefault();
                setToast("正在处理配置，请稍候。");
              } else if (dirty.current) {
                event.preventDefault();
                setExitPending(true);
              }
            },
          );
          if (stopped) unlisten();
          else dispose = unlisten;
        })
        .catch(() => {
          /* beforeunload remains available in browser previews. */
        });
    return () => {
      stopped = true;
      dispose?.();
      window.removeEventListener("beforeunload", beforeUnload);
    };
  }, []);
  async function operate(action: () => Promise<Snapshot>, message: string) {
    mutation.current = true;
    setBusy(true);
    setError("");
    generation.current++;
    try {
      setSnapshot(await action());
      setToast(message);
      setDeleting(null);
    } catch (e) {
      setError(errorText(e));
    } finally {
      mutation.current = false;
      setBusy(false);
    }
  }
  const rows = visibleProfiles(
    snapshot?.profiles ?? [],
    search,
    snapshot?.activeId ?? null,
    sort,
  );
  const active = snapshot?.profiles.find((p) => p.id === snapshot.activeId);
  return (
    <div className="app-shell">
      <Sidebar
        app={app}
        section={section}
        select={setApp}
        setSection={navigate}
        collapsed={collapsed}
        toggle={() => setCollapsed(!collapsed)}
        dark={dark}
        toggleTheme={() => setDark(!dark)}
        about={() => setAbout(true)}
        busy={busy}
      />
      <main id="main-content" className="main-content">
        {section === "providers" && (
          <>
        <AppPageHeader
          app={app}
          actions={
            <>
              <ClientActions app={app} disabled={busy} onError={setClientError} />
              <Button
                variant="quiet"
                disabled={busy || loading || !desktop}
                onClick={() =>
                  void operate(() => api.import(app), "已从本机配置导入。")
                }
              >
                <Download size={15} />
                <span>从本机导入</span>
              </Button>
              <Button disabled={busy} onClick={() => setEditor(emptyProfile())}>
                <Plus size={16} />
                添加提供商
              </Button>
            </>
          }
        />
        <div className="page-scroll">
          <div className="page-content">
            {!desktop && (
              <div className="preview-notice">
                <Info size={15} />
                界面预览 · 在桌面应用中可保存并切换本机配置
              </div>
            )}
            <section
              className={`account-card ${snapshot?.accountMode ? "is-account-active" : ""}`}
              aria-label="账号登录方式"
            >
              <div className="account-icon">
                <UserRound size={22} strokeWidth={1.6} />
              </div>
              <div className="account-info">
                <div className="provider-title">
                  <h2>账号登录</h2>
                  {snapshot?.accountMode && (
                    <span className="active-badge">
                      <span />
                      当前方式
                    </span>
                  )}
                </div>
                <p>
                  使用{app === "codex" ? " ChatGPT " : " Claude "}
                  账号；未登录时在 {appNames[app]} 中完成登录。
                </p>
              </div>
              <Button
                variant="neutral"
                size="compact"
                disabled={
                  busy ||
                  loading ||
                  !snapshot ||
                  snapshot.accountMode ||
                  !desktop
                }
                onClick={() =>
                  void operate(
                    () => api.account(app),
                    `已切换为账号登录，请重启 ${appNames[app]}；未登录时请在应用内完成登录。`,
                  )
                }
              >
                {snapshot?.accountMode ? (
                  <>
                    <Check size={14} />
                    已选择账号登录
                  </>
                ) : (
                  "切换到账号登录"
                )}
              </Button>
            </section>
            <div className="list-toolbar">
              <div className="search-input">
                <Search size={16} />
                <Input
                  aria-label="搜索提供商"
                  placeholder="搜索名称、地址或模型…"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
                {search && (
                  <button aria-label="清空搜索" onClick={() => setSearch("")}>
                    <X size={14} />
                  </button>
                )}
              </div>
              <span className="provider-count">
                {snapshot?.profiles.length ?? 0} 个配置
              </span>
              <select
                aria-label="排序方式"
                value={sort}
                onChange={(e) => setSort(e.target.value)}
              >
                <option value="active">当前配置优先</option>
                <option value="name">按名称排序</option>
              </select>
              <Button
                variant="neutral"
                size="icon"
                aria-label="刷新配置"
                title="刷新配置"
                disabled={busy || loading}
                onClick={() => void refresh()}
              >
                <RefreshCw
                  size={15}
                  className={loading ? "animate-spin" : ""}
                />
              </Button>
            </div>
            {clientError && (
              <div className="error-message page-error" role="alert">
                {clientError}
                <Button variant="quiet" size="compact" onClick={() => setClientError("")}>
                  关闭
                </Button>
              </div>
            )}
            {(error || snapshot?.warning) && (
              <div className="error-message page-error" role="alert">
                {error || snapshot?.warning}
                <Button
                  variant="quiet"
                  size="compact"
                  onClick={() => void refresh()}
                >
                  重试
                </Button>
              </div>
            )}
            {loading ? (
              <div className="loading-state" role="status">
                <Loader2 className="animate-spin" size={22} />
                <span>正在读取配置…</span>
              </div>
            ) : rows.length ? (
              <div className="provider-list" aria-label="提供商列表">
                {rows.map((profile) => (
                  <article
                    key={profile.id}
                    className={`provider-card ${profile.id === snapshot?.activeId ? "is-active" : ""}`}
                    aria-label={profile.name}
                  >
                    <div className="provider-icon">
                      <Globe2 size={23} strokeWidth={1.5} />
                    </div>
                    <div className="provider-info">
                      <div className="provider-title">
                        <h2 title={profile.name}>{profile.name}</h2>
                        {profile.id === snapshot?.activeId && (
                          <span className="active-badge">
                            <span />
                            当前使用
                          </span>
                        )}
                      </div>
                      <div className="provider-url" title={profile.baseUrl}>
                        {profile.baseUrl}
                      </div>
                      <div className="provider-details">
                        <span className="model-tag">
                          <Server size={11} />
                          {profile.model || "自动发现模型"}
                        </span>
                        {profile.notes && (
                          <span className="provider-note" title={profile.notes}>
                            {profile.notes}
                          </span>
                        )}
                      </div>
                    </div>
                    <div className="card-actions">
                      <Button
                        variant="neutral"
                        size="compact"
                        className={
                          profile.id === snapshot?.activeId
                            ? "active-button"
                            : ""
                        }
                        disabled={
                          busy || profile.id === snapshot?.activeId || !desktop
                        }
                        onClick={() =>
                          void operate(
                            () => api.activate(app, profile.id),
                            `已启用「${profile.name}」，请重启 ${appNames[app]} 会话。`,
                          )
                        }
                      >
                        {profile.id === snapshot?.activeId ? (
                          <>
                            <Check size={14} />
                            已启用
                          </>
                        ) : (
                          "启用"
                        )}
                      </Button>
                      <span className="action-divider" />
                      <Button
                        variant="ghost"
                        size="icon-compact"
                        aria-label={`编辑 ${profile.name}`}
                        title="编辑"
                        disabled={busy}
                        onClick={() => setEditor({ ...profile })}
                      >
                        <Pencil size={15} />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon-compact"
                        aria-label={`余额配置 ${profile.name}`}
                        title="余额查询设置"
                        disabled={busy}
                        onClick={() => {
                          setEditorTab("balance");
                          setEditor({ ...profile });
                        }}
                      >
                        <Wallet size={15} />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon-compact"
                        aria-label={`复制 ${profile.name}`}
                        title="复制配置"
                        disabled={busy}
                        onClick={() =>
                          setEditor({
                            ...profile,
                            id: "",
                            name: `${profile.name} 副本`,
                          })
                        }
                      >
                        <Copy size={15} />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon-compact"
                        className="delete-button"
                        aria-label={`删除 ${profile.name}`}
                        title="删除"
                        disabled={busy || !desktop}
                        onClick={() => setDeleting(profile)}
                      >
                        <Trash2 size={15} />
                      </Button>
                    </div>
                    <BalanceDisplay profile={profile} />
                  </article>
                ))}
              </div>
            ) : (
              <div className="empty-state">
                <div className="empty-icon">
                  {search ? (
                    <Search size={28} strokeWidth={1.4} />
                  ) : (
                    <FileJson2 size={29} strokeWidth={1.4} />
                  )}
                </div>
                <h2>
                  {search ? "没有找到匹配的配置" : "添加你的第一个提供商"}
                </h2>
                <p>
                  {search
                    ? "试试其他名称、API 地址或模型。"
                    : "准备好 API 地址和密钥，即可开始配置。"}
                </p>
                {search ? (
                  <Button variant="neutral" onClick={() => setSearch("")}>
                    清空搜索
                  </Button>
                ) : (
                  <Button
                    variant="neutral"
                    onClick={() => setEditor(emptyProfile())}
                  >
                    <Plus size={15} />
                    添加配置
                  </Button>
                )}
              </div>
            )}
            {!loading && rows.length > 0 && (
              <div className="list-caption">
                <CheckCheck size={14} />
                {snapshot?.accountMode
                  ? "当前使用账号登录，启用任一配置可切回 API。"
                  : active
                    ? `当前正在使用「${active.name}」`
                    : "本机配置与已保存条目不同，可重新启用或从本机导入。"}
              </div>
            )}
          </div>
        </div>
        <footer className="config-footer">
          <FileJson2 size={13} />
          <span title={snapshot?.configPath}>
            {snapshot?.configPath || "正在读取配置路径…"}
          </span>
          <span className="local-label">
            <span />
            配置保存在本机
          </span>
        </footer>
          </>
        )}
        <div className={`tool-page-host ${section === "terminal" ? "active" : ""}`}>
          <Suspense fallback={null}>
            {terminalVisited && <TerminalPage />}
          </Suspense>
        </div>
        <div className={`tool-page-host ${section === "skills" ? "active" : ""}`}>
          {skillsVisited && <SkillsPage />}
        </div>
      </main>
      {toast && (
        <div className="toast" role="status">
          <CheckCircle />
          <span>{toast}</span>
          <button aria-label="关闭通知" onClick={() => setToast("")}>
            <X size={14} />
          </button>
        </div>
      )}
      {editor && (
        <ProviderEditor
          app={app}
          initial={editor}
          initialTab={editorTab}
          onDirty={onDirty}
          onBusy={onEditorBusy}
          onClose={() => {
            setEditor(null);
            setEditorTab("basic");
          }}
          onSaved={(next, activated) => {
            generation.current++;
            setSnapshot(next);
            setEditor(null);
            setEditorTab("basic");
            dirty.current = false;
            setToast(
              activated
                ? `已保存并启用，请重启 ${appNames[app]} 会话。`
                : "配置已保存，可在列表中启用。",
            );
          }}
        />
      )}
      {deleting && (
        <ConfirmDialog
          title={`删除「${deleting.name}」？`}
          description={`只删除工具中的配置记录，已写入 ${appNames[app]} 的配置文件会保留。`}
          confirm="删除配置"
          destructive
          busy={busy}
          onCancel={() => setDeleting(null)}
          onConfirm={() =>
            void operate(() => api.delete(app, deleting.id), "配置已删除。")
          }
        />
      )}
      {about && (
        <Dialog
          open
          onOpenChange={(open) => {
            setAbout(open);
            setProjectLinkError("");
          }}
        >
          <DialogContent className="max-w-[440px]">
            <DialogHeader>
              <DialogTitle>API Config Tool</DialogTitle>
              <DialogDescription className="pt-3 leading-6">
                专注 Codex、Claude Code 和 Claude Desktop 的 API 配置。
                <br />
                <br />
                <a
                  href={projectUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="project-link"
                  aria-label="在浏览器中打开 GitHub 项目"
                  title="在默认浏览器中打开 GitHub 项目"
                  onClick={(event) => {
                    if (!desktop) return;
                    event.preventDefault();
                    setProjectLinkError("");
                    void openUrl(projectUrl).catch(() =>
                      setProjectLinkError(
                        "无法打开浏览器，请复制项目地址后访问。",
                      ),
                    );
                  }}
                >
                  <Github size={23} aria-hidden="true" className="shrink-0" />
                  <span className="min-w-0">
                    <span className="project-link-title">GitHub</span>
                    <span className="project-link-url">{projectUrl}</span>
                  </span>
                  <ExternalLink
                    size={14}
                    aria-hidden="true"
                    className="shrink-0"
                  />
                </a>
                <br />
                <br />
                本地配置库：
                <span className="break-all">
                  {snapshot?.storePath || "~/.api-config-tool"}
                </span>
              </DialogDescription>
            </DialogHeader>
            {projectLinkError && (
              <p role="alert" className="error-message">
                {projectLinkError}
              </p>
            )}
            <DialogFooter>
              <Button
                variant="neutral"
                onClick={() => {
                  setAbout(false);
                  setProjectLinkError("");
                }}
              >
                关闭
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
      {exitPending && (
        <ConfirmDialog
          title="退出并放弃修改？"
          description="当前配置尚未保存。"
          confirm="退出"
          onCancel={() => setExitPending(false)}
          onConfirm={() => {
            dirty.current = false;
            void import("@tauri-apps/api/window").then(({ getCurrentWindow }) =>
              getCurrentWindow().destroy(),
            );
          }}
        />
      )}
    </div>
  );
}
function CheckCircle() {
  return (
    <span className="toast-icon">
      <Check size={13} />
    </span>
  );
}
