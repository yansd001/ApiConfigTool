import { useCallback, useEffect, useState } from "react";
import { Loader2, RefreshCw, Wallet } from "lucide-react";
import {
  api,
  defaultBalance,
  desktop,
  errorText,
  type BalanceState,
  type Profile,
  type TopUpInfo,
} from "@/lib/api";
import { RechargeDialog } from "./RechargeDialog";

const amount = (value: number, unit: string) =>
  `${value.toLocaleString("zh-CN", { maximumFractionDigits: 4 })} ${unit}`;
export function BalanceResult({ value }: { value: BalanceState }) {
  return (
    <>
      <div className="balance-values">
        {value.rows.map((row, i) => (
          <span
            key={i}
            title={[
              row.used !== null ? `已用 ${amount(row.used, row.unit)}` : "",
              row.total !== null ? `总额 ${amount(row.total, row.unit)}` : "",
              row.extra,
            ]
              .filter(Boolean)
              .join(" · ")}
          >
            {row.label}{" "}
            <strong>
              {row.unlimited
                ? "不限额"
                : row.remaining !== null
                  ? amount(row.remaining, row.unit)
                  : `已用 ${amount(row.used ?? row.total ?? 0, row.unit)}`}
            </strong>
          </span>
        ))}
      </div>
      {value.error && (
        <span className="balance-error">
          {value.rows.length > 0 ? "更新失败 · 显示上次结果：" : ""}
          {value.error}
        </span>
      )}
    </>
  );
}

export function BalanceDisplay({ profile }: { profile: Profile }) {
  // Remount on query changes so a previous key's result cannot appear on the new key.
  const identity = JSON.stringify([
    profile.baseUrl,
    profile.apiKey,
    profile.balance ?? defaultBalance(),
  ]);
  return <BalanceQuery key={identity} profile={profile} />;
}
function BalanceQuery({ profile }: { profile: Profile }) {
  const config = profile.balance ?? defaultBalance();
  const [value, setValue] = useState<BalanceState | null>(null);
  const [loading, setLoading] = useState(false);
  const [trigger, setTrigger] = useState({ revision: 0, force: false });
  const [clock, setClock] = useState(Date.now());
  const [topupInfo, setTopupInfo] = useState<TopUpInfo | null>(null);
  const [rechargeOpen, setRechargeOpen] = useState(false);
  const refreshAfterTopup = useCallback(() => {
    setTrigger((v) => ({ revision: v.revision + 1, force: true }));
  }, []);
  useEffect(() => {
    if (!desktop) return;
    let cancelled = false;
    void api
      .topupInfo(profile)
      .then((info) => {
        if (!cancelled && info.enabled && info.paymentMethods.length > 0) setTopupInfo(info);
      })
      .catch(() => {
        if (!cancelled) setTopupInfo(null);
      });
    return () => {
      cancelled = true;
    };
  }, [profile.baseUrl, profile.apiKey]);
  useEffect(() => {
    if (!desktop || config.mode === "off") return;
    const timer = setInterval(() => {
      if (config.intervalMinutes > 0)
        setTrigger((v) => ({ revision: v.revision + 1, force: false }));
    }, 30_000);
    const clockTimer = setInterval(() => setClock(Date.now()), 1000);
    return () => {
      clearInterval(timer);
      clearInterval(clockTimer);
    };
  }, [config.mode, config.intervalMinutes]);
  useEffect(() => {
    if (
      !desktop ||
      config.mode === "off" ||
      (config.intervalMinutes === 0 && !trigger.force)
    )
      return;
    let cancelled = false;
    setLoading(true);
    void api
      .balance(profile, trigger.force)
      .then((result) => {
        if (!cancelled) setValue(result);
      })
      .catch((error) => {
        if (!cancelled)
          setValue((old) => ({
            rows: [],
            note: "",
            updatedAt: null,
            nextRetryAt: 0,
            canRefreshAt: 0,
            ...old,
            error: errorText(error),
          }));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // profile query fields are represented by the parent's remount key.
  }, [trigger]);
  if (config.mode === "off" && !topupInfo) return null;
  const details = [
    value?.note,
    value?.updatedAt
      ? `更新于 ${new Date(value.updatedAt * 1000).toLocaleTimeString()}`
      : "",
    value?.error && value.nextRetryAt
      ? `下次自动重试 ${new Date(value.nextRetryAt * 1000).toLocaleTimeString()}`
      : "",
  ]
    .filter(Boolean)
    .join("\n");
  return (
    <div className="balance-strip" title={details}>
      {config.mode !== "off" && (
        <>
          <Wallet size={13} className="balance-icon" />
          <div className="balance-content">
            {value ? (
              <BalanceResult value={value} />
            ) : (
              <span>
                {loading
                  ? "余额查询中…"
                  : config.intervalMinutes === 0
                    ? "余额待查询"
                    : "余额待更新"}
              </span>
            )}
            {value?.note && <span className="balance-note">{value.note}</span>}
          </div>
          <button
            type="button"
            aria-label={`刷新余额 ${profile.name}`}
            title={details || "刷新余额"}
            disabled={
              loading || !desktop || (value?.canRefreshAt ?? 0) > clock / 1000
            }
            onClick={() =>
              setTrigger((v) => ({ revision: v.revision + 1, force: true }))
            }
          >
            {loading ? (
              <Loader2 size={13} className="animate-spin" />
            ) : (
              <RefreshCw size={13} />
            )}
          </button>
        </>
      )}
      {topupInfo && (
        <button
          type="button"
          className="balance-recharge"
          aria-label={`充值 ${profile.name}`}
          title="充值"
          onClick={() => setRechargeOpen(true)}
        >
          充值
        </button>
      )}
      {rechargeOpen && topupInfo && (
        <RechargeDialog
          profile={profile}
          info={topupInfo}
          onPaid={refreshAfterTopup}
          onClose={() => setRechargeOpen(false)}
        />
      )}
    </div>
  );
}
