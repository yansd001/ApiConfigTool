import { invoke, isTauri } from "@tauri-apps/api/core";
import metadata from "../../package.json";
export const appVersion = metadata.version;

export const apps = ["codex", "claude", "claude-desktop"] as const;
export type AppId = (typeof apps)[number];
export type DesktopClient = "vscode" | "chatgpt" | "claude";
export const appNames: Record<AppId, string> = {
  codex: "Codex",
  claude: "Claude Code",
  "claude-desktop": "Claude Desktop",
};
export interface Profile {
  id: string;
  name: string;
  baseUrl: string;
  apiKey: string;
  model: string;
  notes: string;
  configText: string | null;
  balance?: BalanceConfig;
}
export interface BalanceConfig {
  mode: "auto" | "custom" | "off";
  intervalMinutes: number;
  timeoutSeconds: number;
  script: string;
  baseUrl: string;
  apiKey: string;
  accessToken: string;
  userId: string;
}
export const defaultBalance = (): BalanceConfig => ({
  mode: "auto",
  intervalMinutes: 5,
  timeoutSeconds: 15,
  script: "",
  baseUrl: "",
  apiKey: "",
  accessToken: "",
  userId: "",
});
export interface BalanceRow {
  label: string;
  remaining: number | null;
  used: number | null;
  total: number | null;
  unit: string;
  unlimited: boolean;
  extra: string;
}
export interface BalanceState {
  rows: BalanceRow[];
  note: string;
  error: string | null;
  updatedAt: number | null;
  nextRetryAt: number;
  canRefreshAt: number;
}
export interface PaymentMethod {
  name: string;
  kind: string;
}
export interface TopUpInfo {
  enabled: boolean;
  orderManagement: boolean;
  minAmount: number;
  amountOptions: number[];
  paymentMethods: PaymentMethod[];
}
export interface Checkout {
  tradeNo: string;
  amount: number;
  money: number;
  paymentMethod: string;
  payUrl?: string;
  mobilePayUrl?: string;
  qrCode?: string;
  urlScheme?: string;
}
export interface PendingTopUp {
  tradeNo: string;
  amount: number;
  money: number;
  paymentMethod: string;
  createTime: number;
  expiresAt: number;
}
export interface TerminalSession {
  id: string;
  workingDirectory: string;
}
export interface SkillSearchResult {
  id: string;
  source: string;
  skillId: string;
  name: string;
  installs: number;
}
export interface InstalledSkill {
  name: string;
  app: string;
  path: string;
}
export interface Snapshot {
  profiles: Profile[];
  activeId: string | null;
  accountMode: boolean;
  configPath: string;
  storePath: string;
  warning: string | null;
}
export const emptyProfile = (): Profile => ({
  id: "",
  name: "",
  baseUrl: "",
  apiKey: "",
  model: "",
  notes: "",
  configText: null,
});
export const desktop = isTauri();
export const api = {
  runningClients: () => invoke<DesktopClient[]>("running_clients"),
  launchClient: (client: DesktopClient, restart: boolean) =>
    invoke<void>("launch_client", { client, restart }),
  list: (app: AppId) =>
    desktop
      ? invoke<Snapshot>("list_profiles", { app })
      : Promise.resolve<Snapshot>({
          profiles: [],
          activeId: null,
          accountMode: false,
          configPath: "请使用桌面应用读取本机配置",
          storePath: "",
          warning: null,
        }),
  save: (app: AppId, profile: Profile, activate: boolean) =>
    invoke<Snapshot>("save_profile", { app, profile, activate }),
  activate: (app: AppId, id: string) =>
    invoke<Snapshot>("activate_profile", { app, id }),
  account: (app: AppId) => invoke<Snapshot>("switch_to_account", { app }),
  delete: (app: AppId, id: string) =>
    invoke<Snapshot>("delete_profile", { app, id }),
  import: (app: AppId) => invoke<Snapshot>("import_profile", { app }),
  preview: (app: AppId, profile: Profile) =>
    invoke<string>("preview_config", { app, profile }),
  parse: (app: AppId, profile: Profile, source: string) =>
    invoke<Profile>("parse_config", { app, profile, source }),
  models: (app: AppId, baseUrl: string, apiKey: string) =>
    invoke<string[]>("fetch_models", { app, baseUrl, apiKey }),
  balance: (profile: Profile, force = false) =>
    invoke<BalanceState>("fetch_balance", { profile, force }),
  topupInfo: (profile: Profile) =>
    invoke<TopUpInfo>("fetch_topup_info", { profile }),
  createTopup: (profile: Profile, amount: number, paymentMethod: string) =>
    invoke<Checkout>("create_topup", {
      profile,
      amount,
      paymentMethod,
    }),
  topupOrders: (profile: Profile) =>
    invoke<PendingTopUp[]>("fetch_topup_orders", { profile }),
  topupResume: (profile: Profile, tradeNo: string) =>
    invoke<Checkout>("resume_topup", { profile, tradeNo }),
  topupCancel: (profile: Profile, tradeNo: string) =>
    invoke<void>("cancel_topup", { profile, tradeNo }),
  topupStatus: (profile: Profile, tradeNo: string) =>
    invoke<string>("fetch_topup_status", { profile, tradeNo }),
  terminalOpen: (workingDirectory?: string) =>
    invoke<TerminalSession>("open_terminal", {
      workingDirectory: workingDirectory || null,
    }),
  terminalInput: (id: string, data: string) =>
    invoke<void>("terminal_input", { id, data }),
  terminalResize: (id: string, columns: number, rows: number) =>
    invoke<void>("terminal_resize", { id, columns, rows }),
  terminalClose: (id: string) => invoke<void>("close_terminal", { id }),
  skillsList: () => invoke<InstalledSkill[]>("list_skills"),
  skillsSearch: (query: string) =>
    invoke<SkillSearchResult[]>("search_skills", { query }),
  skillsInstall: (source: string, skillId: string, name: string, app: string) =>
    invoke<InstalledSkill>("install_skill", { source, skillId, name, app }),
  skillsRemove: (app: string, name: string) =>
    invoke<void>("remove_skill", { app, name }),
};
export const errorText = (error: unknown) =>
  typeof error === "string"
    ? error
    : error instanceof Error
      ? error.message
      : "操作失败，请重试。";

export function visibleProfiles(
  profiles: Profile[],
  search: string,
  activeId: string | null,
  sort: string,
): Profile[] {
  const needle = search.trim().toLocaleLowerCase();
  const rows = profiles.filter((p) =>
    [p.name, p.baseUrl, p.model, p.notes].some((v) =>
      v.toLocaleLowerCase().includes(needle),
    ),
  );
  return [...rows].sort((a, b) =>
    sort === "name"
      ? a.name.localeCompare(b.name, "zh-CN")
      : Number(b.id === activeId) - Number(a.id === activeId),
  );
}
