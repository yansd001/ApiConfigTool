// Real Windows executable + WebView2 + Tauri IPC. Every application path is isolated.
import { chromium, expect } from "@playwright/test";
import { mkdtemp, mkdir, writeFile, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import { createServer } from "node:net";
import { createServer as createHttpServer } from "node:http";

if (process.platform !== "win32")
  throw new Error("This smoke check targets Windows WebView2.");
const exec = promisify(execFile);
const root = await mkdtemp(join(tmpdir(), "api-config-native-"));
const server = createServer();
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const port = server.address().port;
await new Promise((resolve) => server.close(resolve));
const balanceRequests = [];
const balanceServer = createHttpServer((request, response) => {
  balanceRequests.push({
    url: request.url,
    authorization: request.headers.authorization,
  });
  response.setHeader("Content-Type", "application/json");
  if (request.url === "/gateway/api/usage/token/")
    response.end(
      JSON.stringify({
        data: {
          total_available: 500000,
          total_used: 0,
          unlimited_quota: false,
          user_quota: { total_available: 21250000, total_used: 1000000 },
        },
      }),
    );
  else if (request.url === "/gateway/api/status")
    response.end(
      JSON.stringify({
        data: { quota_per_unit: 500000, quota_display_type: "USD" },
      }),
    );
  else if (request.url === "/custom-balance")
    response.end(JSON.stringify({ balance: 77 }));
  else {
    response.statusCode = 404;
    response.end("{}");
  }
});
await new Promise((resolve) => balanceServer.listen(0, "127.0.0.1", resolve));
balanceServer.unref();
const balanceBase = `http://127.0.0.1:${balanceServer.address().port}`;
for (const name of ["store", "codex", "claude", "local", "webview"])
  await mkdir(join(root, name));
const authFixture = JSON.stringify({
  auth_mode: "chatgpt",
  tokens: {
    access_token: "smoke-account-token",
    refresh_token: "smoke-refresh-token",
  },
});
await writeFile(join(root, "codex", "auth.json"), authFixture);
const claudeCredential = JSON.stringify({
  claudeAiOauth: { accessToken: "smoke-claude-login" },
});
await writeFile(join(root, "claude", ".credentials.json"), claudeCredential);
for (const app of ["codex", "claude", "claude-desktop"]) {
  await writeFile(
    join(root, "store", `${app}.json`),
    JSON.stringify({
      version: 1,
      profiles: [
        {
          id: "native-test",
          name: "原生配置测试",
          baseUrl: `${balanceBase}/gateway/v1`,
          apiKey: "smoke-only-key",
          model: app === "codex" ? "gpt-example" : "claude-sonnet-4-6",
          notes: "临时目录验证",
          configText: null,
        },
      ],
      activeId: null,
    }),
  );
}
const launcher = spawn(
  "pwsh",
  [
    "-NoProfile",
    "-File",
    resolve("tests/native/launch.ps1"),
    "-Executable",
    resolve("src-tauri/target/release/api-config-tool.exe"),
    "-FixturePath",
    root,
    "-DebugPort",
    String(port),
  ],
  { stdio: "ignore", windowsHide: true },
);
launcher.unref();
let pid;
for (let attempt = 0; attempt < 80; attempt++) {
  try {
    pid = Number((await readFile(join(root, "app.pid"), "utf8")).trim());
    if (pid) break;
  } catch {}
  await new Promise((resolve) => setTimeout(resolve, 250));
}
if (!pid) throw new Error("Native launcher did not return a process ID.");
let browser;
try {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    try {
      if (
        (
          await fetch(`http://127.0.0.1:${port}/json/version`, {
            signal: AbortSignal.timeout(1000),
          })
        ).ok
      )
        break;
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const page = browser.contexts()[0].pages()[0];
  const failures = [];
  page.on("pageerror", (error) => failures.push(error.message));
  await expect(
    page.getByRole("article", { name: "原生配置测试" }),
  ).toBeVisible();
  await expect(
    page.getByRole("article", { name: "原生配置测试" }),
  ).toContainText("42.5 USD");
  expect(balanceRequests[0].authorization).toBe("Bearer smoke-only-key");
  expect(
    balanceRequests.find((request) => request.url === "/gateway/api/status")
      .authorization,
  ).toBeUndefined();
  // Assert the displayed destination before allowing any activation.
  await expect(page.locator(".config-footer")).toContainText(
    join(root, "codex"),
  );
  await page.getByRole("button", { name: "启用", exact: true }).click();
  await expect(page.getByText("当前使用", { exact: true })).toBeVisible();
  const codex = await readFile(join(root, "codex", "config.toml"), "utf8");
  expect(codex).toContain('experimental_bearer_token = "smoke-only-key"');
  await page.getByRole("button", { name: "Claude Code", exact: true }).click();
  await expect(page.locator(".config-footer")).toContainText(
    join(root, "claude"),
  );
  await page.getByRole("button", { name: "启用", exact: true }).click();
  await expect(page.getByText("当前使用", { exact: true })).toBeVisible();
  const claude = JSON.parse(
    await readFile(join(root, "claude", "settings.json"), "utf8"),
  );
  expect(claude.env.ANTHROPIC_AUTH_TOKEN).toBe("smoke-only-key");
  await page
    .getByRole("button", { name: "Claude Desktop", exact: true })
    .click();
  await expect(page.locator(".config-footer")).toContainText(
    join(root, "local", "Claude-3p"),
  );
  await page.getByRole("button", { name: "启用", exact: true }).click();
  await expect(page.getByText("当前使用", { exact: true })).toBeVisible();
  const meta = JSON.parse(
    await readFile(
      join(root, "local", "Claude-3p", "configLibrary", "_meta.json"),
      "utf8",
    ),
  );
  expect(meta.appliedId).toBe("50f0a33c-e463-4c45-9eb0-120a99381f21");
  for (const [name, app] of [
    ["Codex", "codex"],
    ["Claude Code", "claude"],
    ["Claude Desktop", "claude-desktop"],
  ]) {
    await page.getByRole("button", { name, exact: true }).click();
    const expectedPath =
      app === "claude-desktop"
        ? join(root, "local", "Claude-3p")
        : join(root, app);
    await expect(page.locator(".config-footer")).toContainText(expectedPath);
    await page.getByRole("button", { name: "切换到账号登录" }).click();
    await expect(
      page.getByRole("button", { name: "已选择账号登录" }),
    ).toBeDisabled();
    expect(
      JSON.parse(await readFile(join(root, "store", `${app}.json`), "utf8"))
        .profiles,
    ).toHaveLength(1);
    if (app === "codex") {
      expect(await readFile(join(root, "codex", "auth.json"), "utf8")).toBe(
        authFixture,
      );
      expect(
        await readFile(join(root, "codex", "config.toml"), "utf8"),
      ).not.toContain("smoke-only-key");
    } else if (app === "claude") {
      expect(
        await readFile(join(root, "claude", ".credentials.json"), "utf8"),
      ).toBe(claudeCredential);
      expect(
        JSON.parse(
          await readFile(join(root, "claude", "settings.json"), "utf8"),
        ).forceLoginMethod,
      ).toBe("claudeai");
    } else {
      expect(
        JSON.parse(
          await readFile(
            join(root, "local", "Claude", "claude_desktop_config.json"),
            "utf8",
          ),
        ).deploymentMode,
      ).toBe("1p");
    }
    await page
      .getByRole("article", { name: "原生配置测试" })
      .getByRole("button", { name: "启用", exact: true })
      .click();
    await expect(page.getByText("当前使用", { exact: true })).toBeVisible();
  }
  await page.getByRole("button", { name: "余额配置 原生配置测试" }).click();
  await page.getByLabel("余额查询方式").selectOption("custom");
  await page.getByLabel("查询 Base URL").fill(balanceBase);
  await page
    .getByLabel("查询脚本")
    .fill(
      '({request:{url:"{{baseUrl}}/custom-balance",headers:{Authorization:"Bearer {{apiKey}}"}},extractor:r=>({remaining:r.balance,unit:"USD"})})',
    );
  await page.getByRole("button", { name: "测试余额查询" }).click();
  await expect(page.getByRole("dialog").getByRole("status")).toContainText(
    "77 USD",
  );
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(
    page.getByRole("article", { name: "原生配置测试" }),
  ).toContainText("77 USD");
  expect(
    JSON.parse(
      await readFile(join(root, "store", "claude-desktop.json"), "utf8"),
    ).profiles[0].balance.mode,
  ).toBe("custom");
  await mkdir("artifacts/ui", { recursive: true });
  await page.screenshot({ path: "artifacts/ui/native-desktop.png" });
  await page.getByRole("button", { name: "编辑 原生配置测试" }).click();
  await page.getByLabel("配置名称").fill("未保存的原生修改");
  const closeRequest = () =>
    page.evaluate(() =>
      window.__TAURI_INTERNALS__.invoke("plugin:event|emit_to", {
        target: { kind: "Window", label: "main" },
        event: "tauri://close-requested",
        payload: null,
      }),
    );
  await closeRequest();
  await expect(page.getByRole("dialog").last()).toContainText("退出并放弃修改");
  await page
    .getByRole("dialog")
    .last()
    .getByRole("button", { name: "取消", exact: true })
    .click();
  await expect(page.getByLabel("配置名称")).toHaveValue("未保存的原生修改");
  await closeRequest();
  const disconnected = new Promise((resolve) =>
    browser.once("disconnected", resolve),
  );
  await page.getByRole("button", { name: "退出", exact: true }).click();
  await Promise.race([
    disconnected,
    new Promise((_, reject) =>
      setTimeout(
        () => reject(new Error("Native close did not complete")),
        8000,
      ),
    ),
  ]);
  expect(failures).toEqual([]);
  console.log(
    "Native smoke passed: API/account switching, credential preservation, automatic and custom balance queries through real IPC and local HTTP, and unsaved close. All files were isolated in a temporary directory.",
  );
} finally {
  balanceServer.closeAllConnections();
  balanceServer.close();
  await browser?.close().catch(() => {});
  if (pid)
    await exec("pwsh", [
      "-NoProfile",
      "-Command",
      `Stop-Process -Id ${pid} -ErrorAction SilentlyContinue`,
    ]).catch(() => {});
}
