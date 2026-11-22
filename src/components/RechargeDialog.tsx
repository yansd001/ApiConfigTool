import { useEffect, useMemo, useRef, useState } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { QRCodeSVG } from "qrcode.react";
import { ExternalLink, Loader2, X } from "lucide-react";
import {
  api,
  errorText,
  type Checkout,
  type Profile,
  type TopUpInfo,
} from "@/lib/api";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "./ui/dialog";

export function RechargeDialog({
  profile,
  info,
  onPaid,
  onClose,
}: {
  profile: Profile;
  info: TopUpInfo;
  onPaid: () => void;
  onClose: () => void;
}) {
  const [amount, setAmount] = useState(String(info.amountOptions[0] ?? info.minAmount ?? 1));
  const [method, setMethod] = useState(info.paymentMethods[0]?.kind ?? "");
  const [checkout, setCheckout] = useState<Checkout | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("pending");
  const completionNotified = useRef(false);
  const qrValue = useMemo(
    () => checkout?.qrCode || checkout?.payUrl || checkout?.mobilePayUrl || "",
    [checkout],
  );
  useEffect(() => {
    if (!checkout?.tradeNo) return;
    let cancelled = false;
    let timer: number | undefined;
    const check = async () => {
      try {
        const next = await api.topupStatus(profile, checkout.tradeNo);
        if (cancelled) return;
        setStatus(next);
        if (next === "success") {
          if (!completionNotified.current) {
            completionNotified.current = true;
            onPaid();
          }
          if (timer !== undefined) window.clearInterval(timer);
        }
      } catch {
        // The payment window can outlive a temporary network interruption.
      }
    };
    void check();
    timer = window.setInterval(() => void check(), 5000);
    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearInterval(timer);
    };
  }, [checkout?.tradeNo, onPaid, profile]);
  async function submit() {
    const parsed = Number(amount);
    if (!Number.isInteger(parsed) || parsed < info.minAmount) {
      setError(`请输入不小于 ${info.minAmount} 的整数美元金额。`);
      return;
    }
    setBusy(true);
    setError("");
    try {
      setCheckout(await api.createTopup(profile, parsed, method));
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="recharge-dialog" onEscapeKeyDown={(event) => { event.preventDefault(); onClose(); }}>
        <DialogHeader className="border-b pb-4">
          <div className="flex items-center gap-3">
            <div className="recharge-icon">$</div>
            <div className="flex-1">
              <DialogTitle>充值</DialogTitle>
              <DialogDescription className="mt-0.5 text-caption">使用 API Key 创建支付订单，金额单位为美元。</DialogDescription>
            </div>
            <Button variant="ghost" size="icon" aria-label="关闭充值" onClick={onClose} disabled={busy}>
              <X size={18} />
            </Button>
          </div>
        </DialogHeader>
        <div className="recharge-body">
          {checkout ? (
            <div className="checkout-result">
              {qrValue ? <QRCodeSVG value={qrValue} size={220} marginSize={2} /> : <div className="qr-missing">支付服务未返回二维码，请打开支付链接。</div>}
              <strong>{status === "success" ? "充值已到账" : `订单已创建 · 应付金额 ${checkout.money.toFixed(2)}`}</strong>
              <span>{status === "success" ? "余额已刷新。" : "请使用对应支付方式扫码完成充值。窗口会自动检查支付状态。"}</span>
              {checkout.payUrl && (
                <Button variant="neutral" size="compact" onClick={() => void openUrl(checkout.payUrl!)}>
                  <ExternalLink size={14} /> 打开支付页面
                </Button>
              )}
              <Button variant="quiet" size="compact" onClick={() => setCheckout(null)}>创建另一笔订单</Button>
            </div>
          ) : (
            <>
              <div>
                <label className="form-label" htmlFor="recharge-amount">充值金额 <span className="required">*</span></label>
                <div className="recharge-amount-input"><span>$</span><Input id="recharge-amount" type="number" min={info.minAmount} step="1" value={amount} onChange={(event) => setAmount(event.target.value)} autoFocus /></div>
                <p className="field-hint">最低充值 ${info.minAmount}。</p>
              </div>
              {!!info.amountOptions.length && <div className="recharge-presets">{info.amountOptions.map((option) => <button key={option} type="button" className={String(option) === amount ? "selected" : ""} onClick={() => setAmount(String(option))}>${option}</button>)}</div>}
              {info.paymentMethods.length > 1 && <div><label className="form-label" htmlFor="recharge-method">支付方式</label><select id="recharge-method" className="recharge-method" value={method} onChange={(event) => setMethod(event.target.value)}>{info.paymentMethods.map((item) => <option value={item.kind} key={item.kind}>{item.name}</option>)}</select></div>}
              {error && <div className="error-message" role="alert">{error}</div>}
            </>
          )}
        </div>
        {!checkout && <DialogFooter><Button variant="neutral" onClick={onClose} disabled={busy}>取消</Button><Button onClick={() => void submit()} disabled={busy || !method}>{busy ? <Loader2 size={15} className="animate-spin" /> : null}确定充值</Button></DialogFooter>}
      </DialogContent>
    </Dialog>
  );
}
