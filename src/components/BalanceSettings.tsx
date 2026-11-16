import { useEffect, useRef, useState } from "react";
import { Loader2 } from "lucide-react";
import {
  api,
  defaultBalance,
  desktop,
  errorText,
  type BalanceConfig,
  type BalanceState,
  type Profile,
} from "@/lib/api";
import { Input, fieldClass } from "./ui/input";
import { Button } from "./ui/button";
import { BalanceResult } from "./BalanceDisplay";

export const genericBalanceScript = `({
  request: {
    url: "{{baseUrl}}/user/balance",
    method: "GET",
    headers: { Authorization: "Bearer {{apiKey}}" }
  },
  extractor: function(response) {
    return {
      remaining: response.balance,
      unit: "USD"
    };
  }
})`;
const newApiScript = `({
  request: {
    url: "{{baseUrl}}".replace(/\\/v1\\/?$/, "") + "/api/user/self",
    method: "GET",
    headers: {
      Authorization: "Bearer {{accessToken}}",
      "New-Api-User": "{{userId}}"
    }
  },
  extractor: function(response) {
    if (!response.success || !response.data) return { isValid: false };
    // 按站点实际 quota_per_unit 调整；此处显示原始额度。
    return {
      planName: "账号余额",
      remaining: response.data.quota,
      used: response.data.used_quota,
      unit: "额度"
    };
  }
})`;

export function BalanceSettings({
  profile,
  onChange,
}: {
  profile: Profile;
  onChange: (value: BalanceConfig) => void;
}) {
  const config = profile.balance ?? defaultBalance();
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<BalanceState | null>(null);
  const [error, setError] = useState("");
  const request = useRef(0);
  const identity = JSON.stringify([profile.baseUrl, profile.apiKey, config]);
  useEffect(() => {
    request.current++;
    setTesting(false);
    setResult(null);
    setError("");
    return () => {
      request.current++;
    };
  }, [identity]);
  const update = (patch: Partial<BalanceConfig>) =>
    onChange({ ...config, ...patch });
  async function testQuery() {
    const generation = ++request.current;
    setTesting(true);
    setResult(null);
    setError("");
    try {
      const value = await api.balance({ ...profile, balance: config }, true);
      if (generation === request.current) setResult(value);
    } catch (e) {
      if (generation === request.current) setError(errorText(e));
    } finally {
      if (generation === request.current) setTesting(false);
    }
  }
  return (
    <div className="form-fields balance-settings">
      <div>
        <label className="form-label" htmlFor="balance-mode">
          余额查询方式
        </label>
        <select
          id="balance-mode"
          className={fieldClass}
          value={config.mode}
          onChange={(e) =>
            update({
              mode: e.target.value as BalanceConfig["mode"],
              ...(e.target.value === "custom" && !config.script
                ? { script: genericBalanceScript }
                : {}),
            })
          }
        >
          <option value="auto">自动查询（New API）</option>
          <option value="custom">自定义脚本</option>
          <option value="off">关闭余额查询</option>
        </select>
        <p className="field-hint">
          默认使用此配置的 API Key。优先显示账号余额，站点未开放权限时显示 Key
          额度。
        </p>
      </div>
      {config.mode !== "off" && (
        <>
          <div className="balance-options">
            <div>
              <label className="form-label" htmlFor="balance-interval">
                自动刷新间隔（分钟）
              </label>
              <Input
                id="balance-interval"
                type="number"
                min={0}
                max={1440}
                required
                value={config.intervalMinutes}
                onChange={(e) =>
                  update({ intervalMinutes: Number(e.target.value) })
                }
              />
              <p className="field-hint">0 表示仅手动查询，默认 5 分钟。</p>
            </div>
            <div>
              <label className="form-label" htmlFor="balance-timeout">
                请求超时（秒）
              </label>
              <Input
                id="balance-timeout"
                type="number"
                min={1}
                max={60}
                required
                value={config.timeoutSeconds}
                onChange={(e) =>
                  update({ timeoutSeconds: Number(e.target.value) })
                }
              />
            </div>
          </div>
          {config.mode === "custom" && (
            <>
              <div>
                <label className="form-label" htmlFor="balance-url">
                  查询 Base URL <span>可选</span>
                </label>
                <Input
                  id="balance-url"
                  value={config.baseUrl}
                  placeholder="留空使用基础配置中的地址"
                  onChange={(e) => update({ baseUrl: e.target.value })}
                />
              </div>
              <div>
                <label className="form-label" htmlFor="balance-key">
                  查询 API Key <span>可选</span>
                </label>
                <Input
                  id="balance-key"
                  type="password"
                  autoComplete="off"
                  value={config.apiKey}
                  placeholder="留空使用基础配置中的密钥"
                  onChange={(e) => update({ apiKey: e.target.value })}
                />
              </div>
              <div className="balance-options">
                <div>
                  <label className="form-label" htmlFor="balance-token">
                    Access Token <span>可选</span>
                  </label>
                  <Input
                    id="balance-token"
                    type="password"
                    autoComplete="off"
                    value={config.accessToken}
                    onChange={(e) => update({ accessToken: e.target.value })}
                  />
                </div>
                <div>
                  <label className="form-label" htmlFor="balance-user">
                    User ID <span>可选</span>
                  </label>
                  <Input
                    id="balance-user"
                    value={config.userId}
                    onChange={(e) => update({ userId: e.target.value })}
                  />
                </div>
              </div>
              <div>
                <div className="balance-script-heading">
                  <label className="form-label" htmlFor="balance-script">
                    查询脚本
                  </label>
                  <div>
                    <button
                      type="button"
                      onClick={() => update({ script: genericBalanceScript })}
                    >
                      通用模板
                    </button>
                    <button
                      type="button"
                      onClick={() => update({ script: newApiScript })}
                    >
                      New API 账号模板
                    </button>
                  </div>
                </div>
                <textarea
                  id="balance-script"
                  className={`${fieldClass} source-input`}
                  spellCheck={false}
                  value={config.script}
                  onChange={(e) => update({ script: e.target.value })}
                  required
                />
                <p className="field-hint">
                  兼容 CC Switch 的 request / extractor 格式；支持{" "}
                  {"{{baseUrl}}"}、{"{{apiKey}}"}、{"{{accessToken}}"}、
                  {"{{userId}}"}。脚本请求指定地址，返回
                  remaining、used、total、unit，可返回多个套餐。
                </p>
              </div>
            </>
          )}
          <div>
            <Button
              type="button"
              variant="neutral"
              disabled={testing || !desktop}
              onClick={() => void testQuery()}
            >
              {testing && <Loader2 size={14} className="animate-spin" />}
              {testing ? "查询中…" : "测试余额查询"}
            </Button>
            <p className="field-hint">
              测试使用当前填写的配置，不会保存或启用。
            </p>
          </div>
          {error && (
            <div role="alert" className="error-message">
              {error}
            </div>
          )}
          {result && (
            <div className="balance-test-result" role="status">
              <BalanceResult value={result} />
              {result.note && <p className="field-hint">{result.note}</p>}
            </div>
          )}
        </>
      )}
    </div>
  );
}
