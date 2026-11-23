import { useCallback, useEffect, useRef, useState } from "react";
import { Check, CircleAlert, Download, Loader2, RefreshCw } from "lucide-react";
import { api, appBuild, appVersion, desktop, errorText } from "@/lib/api";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

export function AppUpdate() {
  const [open, setOpen] = useState(false);
  const [checking, setChecking] = useState(false);
  const [build, setBuild] = useState<number | null>(null);
  const [error, setError] = useState("");
  const [progress, setProgress] = useState("");
  const busy = useRef(false);

  const check = useCallback(async () => {
    if (!desktop || busy.current) return;
    busy.current = true;
    setChecking(true);
    setError("");
    setBuild(null);
    try {
      setBuild(await api.checkForUpdates());
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      busy.current = false;
      setChecking(false);
    }
  }, []);

  useEffect(() => {
    void check();
  }, [check]);

  async function install() {
    if (build === null || busy.current) return;
    busy.current = true;
    setProgress("正在下载更新包…");
    try {
      await api.installUpdate(build, setProgress);
      setProgress("正在重启…");
    } catch (reason) {
      busy.current = false;
      setBuild(null);
      setProgress("");
      setError(errorText(reason));
    }
  }

  const current = `v${appVersion}${appBuild ? ` · #${appBuild}` : ""}`;
  if (!desktop) return <span className="version">v{appVersion}</span>;
  const label = checking
    ? "检查中…"
    : error
      ? "检查更新"
      : build
        ? "发现新版"
        : "已是最新";
  const Icon =
    checking || progress
      ? Loader2
      : error
        ? CircleAlert
        : build
          ? Download
          : Check;
  return (
    <>
      <button
        className={`update-entry ${build ? "available" : ""}`}
        title={`${current} · ${label}`}
        aria-label={`版本更新，${label}`}
        aria-haspopup="dialog"
        onClick={() => {
          setOpen(true);
          if (!build && !error) void check();
        }}
      >
        <Icon
          size={15}
          className={checking || progress ? "animate-spin" : ""}
        />
        <span>
          <small>v{appVersion}</small>
          <span>{label}</span>
        </span>
      </button>
      <Dialog
        open={open}
        onOpenChange={(value) => {
          if (!progress) setOpen(value);
        }}
      >
        <DialogContent className="max-w-[400px]">
          <DialogHeader>
            <DialogTitle>版本更新</DialogTitle>
            <DialogDescription className="pt-2">
              当前版本 {current}
            </DialogDescription>
          </DialogHeader>
          <p className="px-6 pb-3 text-sm" role={error ? "alert" : "status"}>
            {progress ||
              error ||
              (checking
                ? "正在检查更新…"
                : build
                  ? `发现新构建 #${build}。安装后将重启应用，当前终端会话会结束。`
                  : "已是最新版本。")}
          </p>
          <DialogFooter>
            <Button
              variant="neutral"
              disabled={!!progress}
              onClick={() => setOpen(false)}
            >
              {build ? "稍后" : "关闭"}
            </Button>
            {build ? (
              <Button disabled={!!progress} onClick={() => void install()}>
                <Download size={15} />
                更新并重启
              </Button>
            ) : (
              <Button disabled={checking} onClick={() => void check()}>
                <RefreshCw size={15} />
                重新检查
              </Button>
            )}
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
