import { useCallback, useEffect, useRef, useState } from "react";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { FitAddon } from "@xterm/addon-fit";
import { Terminal as XTerminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { FolderOpen, Plus, RotateCcw, TerminalSquare, X } from "lucide-react";
import { api, desktop, errorText, type TerminalSession } from "@/lib/api";
import { Button } from "./ui/button";
import { Input } from "./ui/input";

type Tab = TerminalSession & { title: string };

export function TerminalPage() {
  const [tabs, setTabs] = useState<Tab[]>([]);
  const [active, setActive] = useState<string | null>(null);
  const [path, setPath] = useState("");
  const [error, setError] = useState("");
  const terminalElements = useRef(new Map<string, HTMLDivElement>());
  const terminals = useRef(
    new Map<string, { terminal: XTerminal; fit: FitAddon }>(),
  );
  const pathRef = useRef(path);
  const tabsRef = useRef(tabs);
  const nextTitle = useRef(1);
  const started = useRef(false);
  const pendingOutput = useRef(new Map<string, Uint8Array>());
  const [outputReady, setOutputReady] = useState(false);
  pathRef.current = path;
  tabsRef.current = tabs;

  const create = useCallback(
    async (directory?: string, update?: "Codex" | "Claude Code") => {
      if (!desktop) return;
      setError("");
      try {
        const session = await api.terminalOpen(
          directory || pathRef.current || undefined,
        );
        const title = update ? `更新 ${update}` : `终端 ${nextTitle.current++}`;
        setTabs((old) => [...old, { ...session, title }]);
        setActive(session.id);
        setPath(session.workingDirectory);
        if (update) {
          await api.terminalInput(
            session.id,
            update === "Codex" ? "codex update\r" : "claude update\r",
          );
        }
      } catch (reason) {
        setError(errorText(reason));
      }
    },
    [],
  );

  useEffect(() => {
    if (desktop && outputReady && !started.current) {
      started.current = true;
      void create();
    }
  }, [create, outputReady]);

  useEffect(() => {
    if (!desktop) return;
    let cancelled = false;
    let stop: UnlistenFn | undefined;
    void listen<{ sessionId: string; data: number[] }>(
      "terminal-output",
      (event) => {
        const bytes = Uint8Array.from(event.payload.data);
        const item = terminals.current.get(event.payload.sessionId);
        if (item) item.terminal.write(bytes);
        else {
          const previous = pendingOutput.current.get(event.payload.sessionId);
          if (!previous)
            pendingOutput.current.set(event.payload.sessionId, bytes);
          else {
            const combined = new Uint8Array(previous.length + bytes.length);
            combined.set(previous);
            combined.set(bytes, previous.length);
            pendingOutput.current.set(event.payload.sessionId, combined);
          }
        }
      },
    ).then((unlisten) => {
      if (cancelled) unlisten();
      else {
        stop = unlisten;
        setOutputReady(true);
      }
    });
    return () => {
      cancelled = true;
      stop?.();
    };
  }, []);

  useEffect(() => {
    return () => {
      for (const tab of tabsRef.current) void api.terminalClose(tab.id);
      for (const item of terminals.current.values()) item.terminal.dispose();
    };
  }, []);

  async function chooseFolder() {
    if (!desktop) return;
    const selected = await open({ directory: true, multiple: false });
    if (typeof selected === "string") {
      setPath(selected);
      await create(selected);
    }
  }

  async function close(id: string) {
    await api.terminalClose(id);
    terminals.current.get(id)?.terminal.dispose();
    terminals.current.delete(id);
    setTabs((old) => old.filter((tab) => tab.id !== id));
    if (active === id) {
      const next = tabs.find((tab) => tab.id !== id);
      setActive(next?.id ?? null);
    }
  }

  function enter(command: string) {
    if (!active) return;
    setError("");
    terminals.current.get(active)?.terminal.focus();
    void api
      .terminalInput(active, `${command}\r`)
      .catch((reason) => setError(errorText(reason)));
  }

  const activeTab = tabs.find((tab) => tab.id === active);
  return (
    <div className="tool-page terminal-page">
      <div className="tool-page-header">
        <div>
          <h1>终端</h1>
          <p>在配置工具中使用多个独立终端，会话直接运行在本机。</p>
        </div>
        <Button onClick={() => void create()} disabled={!desktop}>
          <Plus size={16} /> 新终端
        </Button>
      </div>
      <div className="terminal-toolbar">
        <div className="terminal-path">
          <FolderOpen size={15} />
          <Input
            aria-label="终端工作目录"
            value={path}
            onChange={(event) => setPath(event.target.value)}
            placeholder="选择项目文件夹作为终端工作目录"
            onKeyDown={(event) => {
              if (event.key === "Enter") void create(path);
            }}
          />
          <Button
            variant="neutral"
            size="compact"
            onClick={() => void chooseFolder()}
            disabled={!desktop}
          >
            打开项目文件夹
          </Button>
        </div>
        <div
          className="terminal-shortcuts"
          role="group"
          aria-label="终端快捷操作"
        >
          <Button
            variant="quiet"
            size="compact"
            onClick={() => enter("codex")}
            disabled={!active}
          >
            进入 Codex 终端
          </Button>
          <Button
            variant="quiet"
            size="compact"
            onClick={() => enter("claude")}
            disabled={!active}
          >
            进入 Claude 终端
          </Button>
          <Button
            variant="quiet"
            size="compact"
            onClick={() => {
              const value = path.replaceAll("'", "''");
              enter(
                navigator.platform.startsWith("Win")
                  ? `Set-Location -LiteralPath '${value}'`
                  : `cd '${path.replaceAll("'", "'\\''")}'`,
              );
            }}
            disabled={!active}
          >
            跳转到工作目录
          </Button>
          {(["Codex", "Claude Code"] as const).map((tool) => (
            <Button
              key={tool}
              variant="quiet"
              size="compact"
              disabled={!desktop}
              title={`在新终端中更新 ${tool}`}
              onClick={() => void create(undefined, tool)}
            >
              更新 {tool}
            </Button>
          ))}
        </div>
      </div>
      {error && <div className="error-message page-error">{error}</div>}
      {!desktop ? (
        <div className="empty-state tool-empty">
          <TerminalSquare size={30} />
          <h2>请在桌面应用中使用终端</h2>
          <p>浏览器预览不会访问本机终端。</p>
        </div>
      ) : (
        <div className="terminal-workspace">
          <div className="terminal-tabs" role="tablist" aria-label="终端会话">
            {tabs.map((tab) => (
              <div
                key={tab.id}
                className={`terminal-tab ${tab.id === active ? "active" : ""}`}
              >
                <button
                  role="tab"
                  aria-selected={tab.id === active}
                  onClick={() => setActive(tab.id)}
                >
                  <TerminalSquare size={14} /> {tab.title}
                </button>
                <button
                  aria-label={`关闭${tab.title}`}
                  onClick={() => void close(tab.id)}
                >
                  <X size={13} />
                </button>
              </div>
            ))}
            {!tabs.length && (
              <span className="terminal-no-tabs">点击“新终端”开始</span>
            )}
          </div>
          <div className="terminal-surface">
            {tabs.map((tab) => (
              <TerminalView
                key={tab.id}
                id={tab.id}
                active={tab.id === active}
                elements={terminalElements.current}
                terminals={terminals.current}
                pendingOutput={pendingOutput.current}
              />
            ))}
            {!activeTab && (
              <div className="terminal-placeholder">
                <RotateCcw size={22} />
                没有打开的终端会话
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function TerminalView({
  id,
  active,
  elements,
  terminals,
  pendingOutput,
}: {
  id: string;
  active: boolean;
  elements: Map<string, HTMLDivElement>;
  terminals: Map<string, { terminal: XTerminal; fit: FitAddon }>;
  pendingOutput: Map<string, Uint8Array>;
}) {
  const elementRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = elementRef.current;
    if (!element) return;
    const terminal = new XTerminal({
      convertEol: true,
      cursorBlink: true,
      fontFamily: "Cascadia Mono, Consolas, monospace",
      fontSize: 13,
      theme: { background: "#101114", foreground: "#e4e4e7" },
      scrollback: 5000,
    });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(element);
    terminal.onData((data) => void api.terminalInput(id, data));
    terminals.set(id, { terminal, fit });
    const pending = pendingOutput.get(id);
    if (pending) {
      terminal.write(pending);
      pendingOutput.delete(id);
    }
    elements.set(id, element);
    const resize = () => {
      if (element.clientWidth === 0 || element.clientHeight === 0) return;
      fit.fit();
      void api.terminalResize(id, terminal.cols, terminal.rows);
    };
    const observer = new ResizeObserver(resize);
    observer.observe(element);
    requestAnimationFrame(resize);
    return () => {
      observer.disconnect();
      terminal.dispose();
      terminals.delete(id);
      elements.delete(id);
    };
  }, [id, elements, terminals, pendingOutput]);
  useEffect(() => {
    if (!active) return;
    const item = terminals.get(id);
    if (!item) return;
    requestAnimationFrame(() => {
      item.fit.fit();
      void api.terminalResize(id, item.terminal.cols, item.terminal.rows);
      item.terminal.focus();
    });
  }, [id, active, terminals]);
  return (
    <div
      ref={elementRef}
      className={`terminal-instance ${active ? "active" : ""}`}
      aria-hidden={!active}
    />
  );
}
