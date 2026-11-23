// Third-party notices: licenses/THIRD-PARTY-NOTICES.txt.
import {
  ArrowLeftRight,
  PanelLeftClose,
  PanelLeftOpen,
  Moon,
  Sun,
  Info,
  Sparkles,
  TerminalSquare,
  PanelsTopLeft,
} from "lucide-react";
import { apps, appNames, type AppId, type Section } from "@/lib/api";
import { AppUpdate } from "./AppUpdate";
import { AppGlyph } from "./AppGlyph";
import { Button } from "@/components/ui/button";

export function Sidebar({
  app,
  section,
  select,
  setSection,
  collapsed,
  toggle,
  dark,
  toggleTheme,
  about,
  busy,
}: {
  app: AppId;
  section: Section;
  select: (app: AppId) => void;
  setSection: (section: Section) => void;
  collapsed: boolean;
  toggle: () => void;
  dark: boolean;
  toggleTheme: () => void;
  about: () => void;
  busy: boolean;
}) {
  return (
    <aside className={`sidebar ${collapsed ? "collapsed" : ""}`}>
      <div className="sidebar-brand">
        <span className="brand-icon">
          <ArrowLeftRight size={18} />
        </span>
        <strong>API Config</strong>
        <Button
          variant="ghost"
          size="icon-compact"
          aria-label={collapsed ? "展开侧栏" : "收起侧栏"}
          title={collapsed ? "展开侧栏" : "收起侧栏"}
          onClick={toggle}
        >
          {collapsed ? (
            <PanelLeftOpen size={16} />
          ) : (
            <PanelLeftClose size={16} />
          )}
        </Button>
      </div>
      <div className="sidebar-section">应用配置</div>
      <nav aria-label="应用配置">
        {apps.map((id) => (
          <button
            key={id}
            className={`nav-item ${section === "providers" && app === id ? "selected" : ""}`}
            aria-current={section === "providers" && app === id ? "page" : undefined}
            aria-label={appNames[id]}
            title={collapsed ? appNames[id] : undefined}
            disabled={busy}
            onClick={() => {
              select(id);
              setSection("providers");
            }}
          >
            <AppGlyph app={id} size={20} />
            <span>{appNames[id]}</span>
            {section === "providers" && app === id && <span className="nav-dot" />}
          </button>
        ))}
      </nav>
      <div className="sidebar-section tools-section">工具</div>
      <nav aria-label="工具">
        <button
          className={`nav-item ${section === "workspace" ? "selected" : ""}`}
          aria-current={section === "workspace" ? "page" : undefined}
          aria-label="内置 VS Code"
          title={collapsed ? "内置 VS Code" : undefined}
          onClick={() => setSection("workspace")}
        >
          <PanelsTopLeft size={19} />
          <span>内置 VS Code</span>
          {section === "workspace" && <span className="nav-dot" />}
        </button>
        <button
          className={`nav-item ${section === "terminal" ? "selected" : ""}`}
          aria-current={section === "terminal" ? "page" : undefined}
          aria-label="终端"
          title={collapsed ? "终端" : undefined}
          onClick={() => setSection("terminal")}
        >
          <TerminalSquare size={19} />
          <span>终端</span>
          {section === "terminal" && <span className="nav-dot" />}
        </button>
        <button
          className={`nav-item ${section === "skills" ? "selected" : ""}`}
          aria-current={section === "skills" ? "page" : undefined}
          aria-label="Skills 管理"
          title={collapsed ? "Skills 管理" : undefined}
          onClick={() => setSection("skills")}
        >
          <Sparkles size={19} />
          <span>Skills 管理</span>
          {section === "skills" && <span className="nav-dot" />}
        </button>
      </nav>
      <div className="sidebar-bottom">
        <div className="sidebar-note">
          <span className="small-dot" />
          专注每一次配置切换
        </div>
        <div className="sidebar-footer">
          <AppUpdate />
          <Button
            variant="ghost"
            size="icon-compact"
            aria-label={dark ? "切换浅色主题" : "切换深色主题"}
            title={dark ? "切换浅色主题" : "切换深色主题"}
            onClick={toggleTheme}
          >
            {dark ? <Sun size={16} /> : <Moon size={16} />}
          </Button>
          <Button
            variant="ghost"
            size="icon-compact"
            aria-label="关于"
            title="关于"
            onClick={about}
          >
            <Info size={16} />
          </Button>
        </div>
      </div>
    </aside>
  );
}
