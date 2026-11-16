import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "./ui/dialog";
import { Button } from "./ui/button";
export function ConfirmDialog({
  title,
  description,
  confirm = "确认",
  destructive = false,
  busy = false,
  onCancel,
  onConfirm,
}: {
  title: string;
  description: string;
  confirm?: string;
  destructive?: boolean;
  busy?: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !busy) onCancel();
      }}
    >
      <DialogContent
        zIndex="alert"
        className="max-w-[420px]"
        onEscapeKeyDown={(e) => {
          if (busy) e.preventDefault();
        }}
      >
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription className="pt-2 leading-6">
            {description}
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="neutral" onClick={onCancel} disabled={busy}>
            取消
          </Button>
          <Button
            variant={destructive ? "destructive" : "solid"}
            onClick={onConfirm}
            disabled={busy}
          >
            {busy ? "处理中…" : confirm}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
