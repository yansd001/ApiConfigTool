// Simplified from CC Switch's AppPageHeader: native title bar, configuration actions only.
import type { ReactNode } from "react";
import { AppGlyph } from "./AppGlyph";
import { appNames, type AppId } from "@/lib/api";

export function AppPageHeader({
  app,
  actions,
}: {
  app: AppId;
  actions: ReactNode;
}) {
  return (
    <header className="page-header">
      <div className="page-header-title">
        <AppGlyph app={app} />
        <h1>{appNames[app]}</h1>
        <span className="header-divider" />
        <span className="text-fg-3">提供商</span>
      </div>
      <div className="header-actions">{actions}</div>
    </header>
  );
}
// Upstream dialogs use this component for custom title bars. Native window chrome
// handles dragging in this edition, so no invisible overlay captures clicks.
export function ModalDragStrip() {
  return null;
}
