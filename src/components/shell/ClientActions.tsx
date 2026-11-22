import { useCallback, useEffect, useRef, useState } from "react";
import { ExternalLink, Loader2, RotateCw } from "lucide-react";
import {
  api,
  desktop,
  errorText,
  type AppId,
  type DesktopClient,
} from "@/lib/api";
import { Button } from "../ui/button";

const clients: Record<AppId, DesktopClient[]> = {
  codex: ["vscode", "chatgpt"],
  claude: ["vscode"],
  "claude-desktop": ["claude"],
};
const names = { vscode: "VSCode", chatgpt: "ChatGPT", claude: "Claude" };

export function ClientActions({
  app,
  disabled,
  onError,
}: {
  app: AppId;
  disabled: boolean;
  onError: (message: string) => void;
}) {
  const [running, setRunning] = useState<DesktopClient[] | null>(null);
  const [pending, setPending] = useState<DesktopClient | null>(null);
  const [statusError, setStatusError] = useState("");
  const version = useRef(0);
  const refresh = useCallback(async () => {
    const current = ++version.current;
    try {
      const next = await api.runningClients();
      if (current === version.current) {
        setRunning(next);
        setStatusError("");
      }
    } catch (reason) {
      if (current === version.current) {
        setRunning(null);
        setStatusError(errorText(reason));
      }
    }
  }, []);

  useEffect(() => {
    onError("");
    if (!desktop) return;
    const update = () => {
      if (!document.hidden) void refresh();
    };
    update();
    const timer = window.setInterval(update, 5000);
    window.addEventListener("focus", update);
    document.addEventListener("visibilitychange", update);
    return () => {
      ++version.current;
      window.clearInterval(timer);
      window.removeEventListener("focus", update);
      document.removeEventListener("visibilitychange", update);
    };
  }, [app, refresh, onError]);

  async function launch(client: DesktopClient, restart: boolean) {
    setPending(client);
    onError("");
    try {
      await api.launchClient(client, restart);
    } catch (reason) {
      onError(errorText(reason));
    } finally {
      await refresh();
      setPending(null);
    }
  }

  return (
    <>
      {clients[app].map((client) => {
        const restart = running?.includes(client) ?? false;
        const Icon =
          pending === client ? Loader2 : restart ? RotateCw : ExternalLink;
        return (
          <Button
            key={client}
            variant="quiet"
            size="compact"
            title={statusError || undefined}
            disabled={
              disabled || !desktop || running === null || pending !== null
            }
            onClick={() => void launch(client, restart)}
          >
            <Icon
              size={14}
              className={pending === client ? "animate-spin" : undefined}
            />
            {restart ? "重启" : "打开"} {names[client]}
          </Button>
        );
      })}
    </>
  );
}
