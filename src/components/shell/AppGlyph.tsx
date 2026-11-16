// Adapted from CC Switch src/components/shell/AppGlyph.tsx (MIT).
import { Monitor, Terminal } from "lucide-react";
import type { AppId } from "@/lib/api";
import claude from "@/assets/claude.svg";
import openai from "@/assets/openai.svg";

export function AppGlyph({ app, size = 20 }: { app: AppId; size?: number }) {
  const Badge = app === "claude" ? Terminal : Monitor;
  return (
    <span
      className="app-glyph"
      style={{ width: size, height: size }}
      aria-hidden="true"
    >
      <img src={app === "codex" ? openai : claude} alt="" draggable={false} />
      {app !== "codex" && (
        <Badge className="app-badge" size={11} strokeWidth={2.5} />
      )}
    </span>
  );
}
