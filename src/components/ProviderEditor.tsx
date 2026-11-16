import { useEffect, useRef, useState } from "react";
import {
  Code2,
  Eye,
  EyeOff,
  FileSliders,
  Loader2,
  Wallet,
  X,
} from "lucide-react";
import {
  api,
  appNames,
  desktop,
  errorText,
  type AppId,
  type Profile,
  type Snapshot,
} from "@/lib/api";
import { Button } from "./ui/button";
import { Input, fieldClass } from "./ui/input";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "./ui/dialog";
import { AppGlyph } from "./shell/AppGlyph";
import { ConfirmDialog } from "./ConfirmDialog";
import { BalanceSettings } from "./BalanceSettings";

export function ProviderEditor({
  app,
  initial,
  onClose,
  onSaved,
  onDirty,
  onBusy,
  initialTab = "basic",
}: {
  app: AppId;
  initial: Profile;
  onClose: () => void;
  onSaved: (value: Snapshot, activated: boolean) => void;
  onDirty: (dirty: boolean) => void;
  onBusy: (busy: boolean) => void;
  initialTab?: "basic" | "balance";
}) {
  const [draft, setDraft] = useState(initial);
  const [source, setSource] = useState("");
  const baseline = useRef("");
  const [tab, setTab] = useState<"basic" | "source" | "balance">(initialTab);
  const sourceMode = tab === "source";
  const [showKey, setShowKey] = useState(false);
  const [busy, setBusy] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [models, setModels] = useState<string[]>([]);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [discard, setDiscard] = useState(false);
  const request = useRef(0);
  const dirty =
    JSON.stringify(initial) !== JSON.stringify(draft) ||
    (sourceMode && source !== baseline.current);
  useEffect(() => {
    onDirty(dirty);
    return () => onDirty(false);
  }, [dirty, onDirty]);
  useEffect(
    () => () => {
      request.current++;
    },
    [],
  );
  useEffect(() => {
    onBusy(busy);
    return () => onBusy(false);
  }, [busy, onBusy]);
  const update = (key: keyof Profile, value: string) => {
    setDraft((old) => ({ ...old, [key]: value }));
    setError("");
    setNotice("");
    if (key === "baseUrl" || key === "apiKey") {
      request.current++;
      setFetching(false);
      setModels([]);
    }
  };
  const close = () => {
    if (!busy) {
      if (dirty) setDiscard(true);
      else onClose();
    }
  };
  async function switchMode(next: "basic" | "source" | "balance") {
    if (next === tab) return;
    setError("");
    setNotice("");
    setBusy(true);
    try {
      if (next === "source") {
        const preview = await api.preview(app, draft);
        setSource(preview);
        baseline.current = preview;
      } else if (sourceMode && source !== baseline.current) {
        setDraft(await api.parse(app, draft, source));
      }
      setTab(next);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }
  async function save(activate: boolean) {
    setError("");
    setNotice("");
    setBusy(true);
    try {
      let profile = draft;
      if (sourceMode && source !== baseline.current) {
        profile = await api.parse(app, draft, source);
      } else if (profile.configText !== null) {
        profile = { ...profile, configText: await api.preview(app, profile) };
      }
      const snapshot = await api.save(app, profile, activate);
      onSaved(snapshot, activate);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }
  async function fetchModels() {
    const generation = ++request.current;
    setFetching(true);
    setError("");
    setNotice("");
    try {
      const rows = await api.models(app, draft.baseUrl, draft.apiKey);
      if (generation === request.current) {
        setModels(rows);
        setNotice(`已获取 ${rows.length} 个模型，可在模型输入框中选择。`);
      }
    } catch (e) {
      if (generation === request.current) setError(errorText(e));
    } finally {
      if (generation === request.current) setFetching(false);
    }
  }
  async function validateSource() {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await api.parse(app, draft, source);
      setNotice("配置校验通过。");
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }
  const fileName =
    app === "codex"
      ? "config.toml"
      : app === "claude"
        ? "settings.json"
        : "configLibrary / profile.json";
  return (
    <>
      <Dialog
        open
        onOpenChange={(open) => {
          if (!open) close();
        }}
      >
        <DialogContent
          className="editor-dialog max-w-[640px]"
          onEscapeKeyDown={(e) => {
            e.preventDefault();
            close();
          }}
        >
          <DialogHeader className="border-b pb-4">
            <div className="flex items-center gap-3">
              <span className="editor-app-icon">
                <AppGlyph app={app} size={23} />
              </span>
              <div className="flex-1">
                <DialogTitle>
                  {initial.id ? "编辑提供商" : "添加提供商"}
                </DialogTitle>
                <DialogDescription className="mt-0.5 text-caption">
                  {appNames[app]} 的 API 配置
                </DialogDescription>
              </div>
              <Button
                variant="ghost"
                size="icon"
                aria-label="关闭编辑器"
                onClick={close}
                disabled={busy}
              >
                <X size={18} />
              </Button>
            </div>
          </DialogHeader>
          <div className="editor-tabs" role="tablist" aria-label="配置编辑方式">
            <button
              role="tab"
              aria-selected={tab === "basic"}
              onClick={() => void switchMode("basic")}
              disabled={busy}
            >
              <FileSliders size={15} />
              基础配置
            </button>
            <button
              role="tab"
              aria-selected={sourceMode}
              onClick={() => void switchMode("source")}
              disabled={busy || !desktop}
            >
              <Code2 size={15} />
              配置源码
            </button>
            <button
              role="tab"
              aria-selected={tab === "balance"}
              onClick={() => void switchMode("balance")}
              disabled={busy}
            >
              <Wallet size={15} />
              余额查询
            </button>
          </div>
          <form
            id="provider-form"
            className="editor-body"
            onSubmit={(e) => {
              e.preventDefault();
              void save(false);
            }}
          >
            <fieldset disabled={busy} className="min-w-0">
              {tab === "balance" ? (
                <BalanceSettings
                  profile={draft}
                  onChange={(balance) =>
                    setDraft((old) => ({ ...old, balance }))
                  }
                />
              ) : !sourceMode ? (
                <div className="form-fields">
                  <div>
                    <label className="form-label" htmlFor="provider-name">
                      配置名称 <span>可选</span>
                    </label>
                    <Input
                      id="provider-name"
                      value={draft.name}
                      onChange={(e) => update("name", e.target.value)}
                      placeholder="例如：日常开发"
                      maxLength={120}
                    />
                  </div>
                  <div>
                    <label className="form-label" htmlFor="base-url">
                      Base URL <span className="required">*</span>
                    </label>
                    <Input
                      id="base-url"
                      value={draft.baseUrl}
                      onChange={(e) => update("baseUrl", e.target.value)}
                      placeholder={
                        app === "codex"
                          ? "https://api.example.com/v1"
                          : "https://api.example.com"
                      }
                      required
                    />
                    <p className="field-hint">
                      填写服务商提供的完整接口地址，自定义路径会原样保留。
                    </p>
                  </div>
                  <div>
                    <label className="form-label" htmlFor="api-key">
                      API Key <span className="required">*</span>
                    </label>
                    <div className="key-input">
                      <Input
                        id="api-key"
                        aria-label="API Key"
                        type={showKey ? "text" : "password"}
                        value={draft.apiKey}
                        onChange={(e) => update("apiKey", e.target.value)}
                        placeholder="输入 API Key"
                        className="pr-11"
                        required
                      />
                      <button
                        type="button"
                        aria-label={showKey ? "隐藏 API Key" : "显示 API Key"}
                        title={showKey ? "隐藏 API Key" : "显示 API Key"}
                        onClick={() => setShowKey(!showKey)}
                      >
                        {showKey ? <EyeOff size={16} /> : <Eye size={16} />}
                      </button>
                    </div>
                  </div>
                  <div>
                    <div className="flex items-center justify-between mb-2">
                      <label className="form-label !mb-0" htmlFor="model">
                        模型名称{" "}
                        {app === "claude-desktop" ? (
                          <span>可选</span>
                        ) : (
                          <span className="required">*</span>
                        )}
                      </label>
                      <button
                        type="button"
                        className="text-action-text text-caption inline-flex items-center gap-1"
                        disabled={
                          fetching ||
                          !draft.baseUrl ||
                          !draft.apiKey ||
                          !desktop
                        }
                        onClick={() => void fetchModels()}
                      >
                        {fetching && (
                          <Loader2 size={13} className="animate-spin" />
                        )}
                        {fetching ? "获取中…" : "获取模型列表"}
                      </button>
                    </div>
                    <div className="relative">
                      <Input
                        id="model"
                        list="available-models"
                        value={draft.model}
                        onChange={(e) => update("model", e.target.value)}
                        placeholder={
                          app === "claude-desktop"
                            ? "留空使用服务端模型发现"
                            : "输入或选择模型 ID"
                        }
                        required={app !== "claude-desktop"}
                      />
                      <datalist id="available-models">
                        {models.map((model) => (
                          <option key={model} value={model} />
                        ))}
                      </datalist>
                    </div>
                    <p className="field-hint">
                      {app === "claude-desktop"
                        ? "需要支持 Anthropic 协议的服务；指定模型时请使用 Claude 模型 ID。"
                        : "模型名称需与服务商提供的模型 ID 一致。"}
                    </p>
                  </div>
                  <div>
                    <label className="form-label" htmlFor="notes">
                      备注 <span>可选</span>
                    </label>
                    <Input
                      id="notes"
                      value={draft.notes}
                      onChange={(e) => update("notes", e.target.value)}
                      placeholder="为这套配置添加简短说明"
                      maxLength={240}
                    />
                  </div>
                </div>
              ) : (
                <div className="source-panel">
                  <div className="source-toolbar">
                    <span>{fileName}</span>
                    <Button
                      type="button"
                      variant="neutral"
                      size="compact"
                      onClick={() => void validateSource()}
                      disabled={busy}
                    >
                      校验配置
                    </Button>
                  </div>
                  <p className="field-hint mb-3">
                    源码包含明文密钥。手动修改后，启用时将写入这份完整配置。
                  </p>
                  <textarea
                    aria-label="配置源码"
                    className={`${fieldClass} source-input`}
                    spellCheck={false}
                    value={source}
                    onChange={(e) => {
                      setSource(e.target.value);
                      setError("");
                      setNotice("");
                    }}
                  />
                </div>
              )}
            </fieldset>
          </form>
          <div className="editor-feedback" aria-live="polite">
            {error && (
              <div role="alert" className="error-message">
                {error}
              </div>
            )}
            {notice && (
              <div role="status" className="success-message">
                {notice}
              </div>
            )}
          </div>
          <div className="editor-footer">
            <span className="text-caption text-fg-3">保存后可随时切换</span>
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="quiet"
                onClick={close}
                disabled={busy}
              >
                取消
              </Button>
              <Button
                type="submit"
                form="provider-form"
                variant="neutral"
                disabled={busy || !desktop}
              >
                保存
              </Button>
              <Button
                type="button"
                onClick={() => {
                  const form = document.getElementById(
                    "provider-form",
                  ) as HTMLFormElement;
                  if (form.reportValidity()) void save(true);
                }}
                disabled={busy || !desktop}
              >
                {busy && <Loader2 size={14} className="animate-spin" />}
                保存并启用
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
      {discard && (
        <ConfirmDialog
          title="放弃未保存的修改？"
          description="这次修改还没有保存到配置列表。"
          confirm="放弃修改"
          onCancel={() => setDiscard(false)}
          onConfirm={onClose}
        />
      )}
    </>
  );
}
