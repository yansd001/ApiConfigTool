// CC Switch's sidebar structure, reduced to the three supported applications.
import {
  ArrowLeftRight,
  PanelLeftClose,
  PanelLeftOpen,
  Moon,
  Sun,
  Info,
} from "lucide-react";
import { apps, appNames, appVersion, type AppId } from "@/lib/api";
import { AppGlyph } from "./AppGlyph";
import { Button } from "@/components/ui/button";

export function Sidebar({
  app,
  select,
  collapsed,
  toggle,
  dark,
  toggleTheme,
  about,
  busy,
}: {
  app: AppId;
  select: (app: AppId) => void;
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
            className={`nav-item ${app === id ? "selected" : ""}`}
            aria-current={app === id ? "page" : undefined}
            aria-label={appNames[id]}
            title={collapsed ? appNames[id] : undefined}
            disabled={busy}
            onClick={() => select(id)}
          >
            <AppGlyph app={id} size={20} />
            <span>{appNames[id]}</span>
            {app === id && <span className="nav-dot" />}
          </button>
        ))}
      </nav>
      <div className="sidebar-bottom">
        <div className="sidebar-note">
          <span className="small-dot" />
          专注每一次配置切换
        </div>
        <div className="sidebar-footer">
          <span className="version">v{appVersion}</span>
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
