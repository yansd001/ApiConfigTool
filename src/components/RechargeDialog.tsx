import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { QRCodeSVG } from "qrcode.react";
import { ExternalLink, Loader2, X } from "lucide-react";
import {
  api,
  errorText,
  type Checkout,
  type PendingTopUp,
  type Profile,
  type TopUpInfo,
} from "@/lib/api";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import { ConfirmDialog } from "./ConfirmDialog";
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
  const [amount, setAmount] = useState(
    String(info.amountOptions[0] ?? info.minAmount ?? 1),
  );
  const [method, setMethod] = useState(info.paymentMethods[0]?.kind ?? "");
  const [checkout, setCheckout] = useState<Checkout | null>(null);
  const [orders, setOrders] = useState<PendingTopUp[]>([]);
  const [ordersLoaded, setOrdersLoaded] = useState(!info.orderManagement);
  const [ordersLoading, setOrdersLoading] = useState(info.orderManagement);
  const [busyOrder, setBusyOrder] = useState("");
  const [cancelTarget, setCancelTarget] = useState<PendingTopUp | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [status, setStatus] = useState("pending");
  const completionNotified = useRef(false);
  const refreshPendingOrders = useCallback(async () => {
    if (!info.orderManagement) return;
    setOrdersLoading(true);
    try {
      setOrders(await api.topupOrders(profile));
      setOrdersLoaded(true);
      setError("");
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setOrdersLoading(false);
    }
  }, [info.orderManagement, profile]);
  const qrValue = useMemo(
    () => checkout?.qrCode || checkout?.payUrl || checkout?.mobilePayUrl || "",
    [checkout],
  );
  useEffect(() => {
    if (!info.orderManagement) return;
    void refreshPendingOrders();
  }, [info.orderManagement, refreshPendingOrders]);
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
          setOrders((old) =>
            old.filter((order) => order.tradeNo !== checkout.tradeNo),
          );
          void refreshPendingOrders();
          if (timer !== undefined) window.clearInterval(timer);
        } else if (
          next === "expired" ||
          next === "cancelled" ||
          next === "failed"
        ) {
          setOrders((old) =>
            old.filter((order) => order.tradeNo !== checkout.tradeNo),
          );
          void refreshPendingOrders();
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
  }, [checkout?.tradeNo, onPaid, profile, refreshPendingOrders]);

  async function resumeOrder(order: PendingTopUp) {
    setBusyOrder(order.tradeNo);
    setError("");
    try {
      setCheckout(await api.topupResume(profile, order.tradeNo));
      setStatus("pending");
    } catch (reason) {
      setError(errorText(reason));
      void refreshPendingOrders();
    } finally {
      setBusyOrder("");
    }
  }

  async function cancelOrder(order: PendingTopUp) {
    setBusyOrder(order.tradeNo);
    setError("");
    try {
      await api.topupCancel(profile, order.tradeNo);
      setOrders((old) => old.filter((item) => item.tradeNo !== order.tradeNo));
      if (checkout?.tradeNo === order.tradeNo) setCheckout(null);
      setNotice("待支付订单已取消。");
      setCancelTarget(null);
    } catch (reason) {
      setError(errorText(reason));
      void refreshPendingOrders();
    } finally {
      setBusyOrder("");
    }
  }
  async function submit() {
    const parsed = Number(amount);
    if (!Number.isInteger(parsed) || parsed < info.minAmount) {
      setError(`请输入不小于 ${info.minAmount} 的整数美元金额。`);
      return;
    }
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const next = await api.createTopup(profile, parsed, method);
      if (!next.tradeNo) throw "充值接口没有返回订单号。";
      setCheckout(next);
      setStatus("pending");
      setOrders((old) => [
        {
          tradeNo: next.tradeNo,
          amount: next.amount,
          money: next.money,
          paymentMethod: next.paymentMethod,
          createTime: Math.floor(Date.now() / 1000),
          expiresAt: Math.floor(Date.now() / 1000) + 30 * 60,
        },
        ...old,
      ]);
      void refreshPendingOrders();
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        className="recharge-dialog"
        onEscapeKeyDown={(event) => {
          event.preventDefault();
          onClose();
        }}
      >
        <DialogHeader className="border-b pb-4">
          <div className="flex items-center gap-3">
            <div className="recharge-icon">$</div>
            <div className="flex-1">
              <DialogTitle>充值</DialogTitle>
              <DialogDescription className="mt-0.5 text-caption">
                使用 API Key 创建支付订单，金额单位为美元。
              </DialogDescription>
            </div>
            <Button
              variant="ghost"
              size="icon"
              aria-label="关闭充值"
              onClick={onClose}
              disabled={busy}
            >
              <X size={18} />
            </Button>
          </div>
        </DialogHeader>
        <div className="recharge-body">
          {checkout ? (
            <div className="checkout-result">
              {status === "pending" &&
                (qrValue ? (
                  <QRCodeSVG value={qrValue} size={220} marginSize={2} />
                ) : (
                  <div className="qr-missing">
                    支付服务未返回二维码，请打开支付链接。
                  </div>
                ))}
              <strong>
                {status === "success"
                  ? "充值已到账"
                  : status === "expired"
                    ? "订单已过期"
                    : status === "cancelled"
                      ? "订单已取消"
                      : status === "failed"
                        ? "支付失败"
                        : `订单待支付 · 应付金额 ${checkout.money.toFixed(2)}`}
              </strong>
              <span>
                {status === "success"
                  ? "余额已刷新。"
                  : status === "pending"
                    ? info.orderManagement
                      ? "关闭窗口后可从充值入口继续支付或取消订单。"
                      : "请扫码完成充值，窗口会自动检查支付状态。"
                    : "可以返回后重新创建充值订单。"}
              </span>
              {status === "pending" && checkout.payUrl && (
                <Button
                  variant="neutral"
                  size="compact"
                  onClick={() => void openUrl(checkout.payUrl!)}
                >
                  <ExternalLink size={14} /> 打开支付页面
                </Button>
              )}
              <div className="checkout-actions">
                {status === "pending" && info.orderManagement && (
                  <Button
                    variant="quiet"
                    size="compact"
                    onClick={() =>
                      setCancelTarget(
                        orders.find(
                          (order) => order.tradeNo === checkout.tradeNo,
                        ) ?? {
                          tradeNo: checkout.tradeNo,
                          amount: checkout.amount,
                          money: checkout.money,
                          paymentMethod: checkout.paymentMethod,
                          createTime: 0,
                          expiresAt: 0,
                        },
                      )
                    }
                    disabled={busyOrder === checkout.tradeNo}
                  >
                    取消订单
                  </Button>
                )}
                <Button
                  variant="quiet"
                  size="compact"
                  onClick={() => setCheckout(null)}
                >
                  {status === "pending" && info.orderManagement
                    ? "返回待支付订单"
                    : "返回充值"}
                </Button>
              </div>
            </div>
          ) : ordersLoading && !ordersLoaded ? (
            <div className="pending-topups-state">
              <Loader2 size={16} className="animate-spin" />
              正在检查待支付订单…
            </div>
          ) : orders.length > 0 ? (
            <div className="pending-topups">
              <div>
                <strong>待支付订单</strong>
                <span>继续付款或取消订单后再创建新的充值。</span>
              </div>
              {orders.map((order) => (
                <article className="pending-topup" key={order.tradeNo}>
                  <div>
                    <strong>
                      ${order.amount} · {order.money.toFixed(2)}
                    </strong>
                    <span>
                      {info.paymentMethods.find(
                        (item) => item.kind === order.paymentMethod,
                      )?.name ?? order.paymentMethod}{" "}
                      · 截止{" "}
                      {new Date(order.expiresAt * 1000).toLocaleTimeString()}
                    </span>
                  </div>
                  <div className="pending-topup-actions">
                    <Button
                      size="compact"
                      onClick={() => void resumeOrder(order)}
                      disabled={!!busyOrder}
                    >
                      {busyOrder === order.tradeNo ? (
                        <Loader2 size={14} className="animate-spin" />
                      ) : null}
                      继续支付
                    </Button>
                    <Button
                      variant="quiet"
                      size="compact"
                      onClick={() => setCancelTarget(order)}
                      disabled={!!busyOrder}
                    >
                      取消
                    </Button>
                  </div>
                </article>
              ))}
            </div>
          ) : ordersLoaded ? (
            <>
              <div>
                <label className="form-label" htmlFor="recharge-amount">
                  充值金额 <span className="required">*</span>
                </label>
                <div className="recharge-amount-input">
                  <span>$</span>
                  <Input
                    id="recharge-amount"
                    type="number"
                    min={info.minAmount}
                    step="1"
                    value={amount}
                    onChange={(event) => setAmount(event.target.value)}
                    autoFocus
                  />
                </div>
                <p className="field-hint">最低充值 ${info.minAmount}。</p>
              </div>
              {!!info.amountOptions.length && (
                <div className="recharge-presets">
                  {info.amountOptions.map((option) => (
                    <button
                      key={option}
                      type="button"
                      className={String(option) === amount ? "selected" : ""}
                      onClick={() => setAmount(String(option))}
                    >
                      ${option}
                    </button>
                  ))}
                </div>
              )}
              {info.paymentMethods.length > 1 && (
                <div>
                  <label className="form-label" htmlFor="recharge-method">
                    支付方式
                  </label>
                  <select
                    id="recharge-method"
                    className="recharge-method"
                    value={method}
                    onChange={(event) => setMethod(event.target.value)}
                  >
                    {info.paymentMethods.map((item) => (
                      <option value={item.kind} key={item.kind}>
                        {item.name}
                      </option>
                    ))}
                  </select>
                </div>
              )}
            </>
          ) : (
            <div className="pending-topups-state">
              <span>读取待支付订单失败，请重试后再创建订单。</span>
              <Button
                variant="neutral"
                size="compact"
                onClick={() => void refreshPendingOrders()}
              >
                重试
              </Button>
            </div>
          )}
          {notice && <div className="success-message">{notice}</div>}
          {error && (
            <div className="error-message" role="alert">
              {error}
            </div>
          )}
        </div>
        {!checkout && ordersLoaded && orders.length === 0 && (
          <DialogFooter>
            <Button variant="neutral" onClick={onClose} disabled={busy}>
              关闭
            </Button>
            <Button
              onClick={() => void submit()}
              disabled={busy || !method || ordersLoading}
            >
              {busy ? <Loader2 size={15} className="animate-spin" /> : null}
              确定充值
            </Button>
          </DialogFooter>
        )}
      </DialogContent>
      {cancelTarget && (
        <ConfirmDialog
          title="取消待支付订单？"
          description="取消后此付款码将不能继续使用。如果支付渠道已经扣款，订单仍可能在支付回调后到账。"
          confirm="取消订单"
          destructive
          busy={busyOrder === cancelTarget.tradeNo}
          onCancel={() => setCancelTarget(null)}
          onConfirm={() => void cancelOrder(cancelTarget)}
        />
      )}
    </Dialog>
  );
}
