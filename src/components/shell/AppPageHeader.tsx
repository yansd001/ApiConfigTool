// Third-party notices: licenses/THIRD-PARTY-NOTICES.txt.
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
// Native window chrome handles dragging; the dialog needs no drag overlay.
export function ModalDragStrip() {
  return null;
}
