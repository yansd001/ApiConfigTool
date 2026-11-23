import { expect, test, type Page } from "@playwright/test";

async function mockDesktop(
  page: Page,
  topupEnabled = false,
  topupUnsupported = false,
) {
  await page.addInitScript(([topupEnabled, topupUnsupported]) => {
    const profiles: Record<string, any[]> = {
      codex: [
        {
          id: "daily",
          name: "日常开发",
          baseUrl: "https://api.example.com/v1",
          apiKey: "test-daily-key",
          model: "gpt-example",
          notes: "开发与日常任务",
          configText: null,
        },
        {
          id: "backup",
          name: "备用服务",
          baseUrl: "https://backup.example.com/v1",
          apiKey: "test-backup-key",
          model: "gpt-example-mini",
          notes: "备用接口",
          configText: null,
        },
        {
          id: "local",
          name: "测试环境",
          baseUrl: "https://staging.example.com/v1",
          apiKey: "test-staging-key",
          model: "gpt-example",
          notes: "开发测试专用",
          configText: null,
        },
      ],
      claude: [],
      "claude-desktop": [],
    };
    const active: Record<string, string | null> = {
      codex: "daily",
      claude: null,
      "claude-desktop": null,
    };
    const calls: { command: string; args: any }[] = [];
    const installedSkills: { name: string; app: string; path: string }[] = [];
    const accountMode: Record<string, boolean> = {
      codex: false,
      claude: true,
      "claude-desktop": true,
    };
    const snapshot = (app: string) =>
      JSON.parse(
        JSON.stringify({
          profiles: profiles[app],
          activeId: active[app],
          accountMode: accountMode[app],
          configPath: `C:\\Users\\demo\\.${app}\\${app === "codex" ? "config.toml" : "settings.json"}`,
          storePath: `C:\\Users\\demo\\.api-config-tool\\${app}.json`,
          warning: null,
        }),
      );
    Object.defineProperty(window, "isTauri", { value: true });
    (window as any).testCalls = calls;
    const runningClients = new Set(["vscode"]);
    let workspaceInstalled = false;
    let recentProjects = ["C:\\已有项目"];
    (window as any).runningClients = runningClients;
    (window as any).__TAURI_INTERNALS__ = {
      metadata: {
        currentWindow: { label: "main" },
        currentWebview: { label: "main" },
      },
      transformCallback: () => 1,
      unregisterCallback: () => {},
      invoke: async (command: string, args: any) => {
        calls.push({ command, args });
        const app = args?.app;
        if (command === "check_for_updates") {
          if ((window as any).checkError) throw (window as any).checkError;
          return (window as any).updateBuild ?? null;
        }
        if (command === "install_update") {
          if ((window as any).installError) throw (window as any).installError;
          return;
        }
        if (command === "workspace_status") return workspaceInstalled;
        if (command === "recent_workspace_projects") return [...recentProjects];
        if (command === "plugin:dialog|open") return "C:\\project with spaces";
        if (command === "install_workspace") {
          workspaceInstalled = true;
          return;
        }
        if (command === "open_workspace") {
          if (args.project) recentProjects = [args.project, ...recentProjects.filter(path => path !== args.project)];
          return;
        }
        if (command === "running_clients") return [...runningClients];
        if (command === "launch_client") {
          if ((window as any).launchError) throw (window as any).launchError;
          runningClients.add(args.client);
          return;
        }
        if (command === "list_profiles") return snapshot(app);
        if (command === "fetch_balance") {
          if ((window as any).failBalance)
            return {
              rows: [],
              note: "",
              error: "站点不支持此余额接口，可配置自定义查询",
              updatedAt: null,
              nextRetryAt: 0,
              canRefreshAt: 0,
            };
          return {
            rows: [
              {
                label:
                  args.profile.balance?.mode === "custom"
                    ? "自定义余额"
                    : "账号余额",
                remaining: 42.5,
                used: 7.5,
                total: null,
                unit: "USD",
                unlimited: false,
                extra: "",
              },
            ],
            note: "",
            error: null,
            updatedAt: Math.floor(Date.now() / 1000),
            nextRetryAt: 0,
            canRefreshAt: 0,
          };
        }
        if (command === "fetch_topup_info" && topupUnsupported)
          throw "充值接口返回 HTTP 404。";
        if (command === "fetch_topup_info")
          return {
            enabled: topupEnabled,
            minAmount: 1,
            amountOptions: [5, 10, 20],
            paymentMethods: topupEnabled
              ? [{ name: "支付宝", kind: "alipay" }]
              : [],
          };
        if (command === "create_topup")
          return {
            tradeNo: "USR1NOtest123",
            amount: args.amount,
            money: args.amount * 7.3,
            paymentMethod: args.paymentMethod,
            qrCode: "https://pay.example/checkout/123",
          };
        if (command === "fetch_topup_status") return "pending";
        if (command === "list_skills") return [...installedSkills];
        if (command === "search_skills")
          return [
            {
              id: "example/skills/sample",
              source: "example/skills",
              skillId: "sample",
              name: "sample",
              installs: 120,
            },
          ];
        if (command === "install_skill") {
          const installed = {
            name: args.skillId,
            app: args.app,
            path: `C:\\Users\\demo\\.${args.app}\\skills\\${args.skillId}`,
          };
          installedSkills.push(installed);
          return installed;
        }
        if (command === "open_terminal")
          return {
            id: crypto.randomUUID(),
            workingDirectory: args.workingDirectory || "C:\\Users\\demo",
          };
        if (command === "save_profile") {
          const p = { ...args.profile };
          if (!p.baseUrl.startsWith("http") || !p.apiKey)
            throw "请填写有效的地址和密钥。";
          if (!p.id) p.id = crypto.randomUUID();
          const index = profiles[app].findIndex((x) => x.id === p.id);
          if (index < 0) profiles[app].push(p);
          else profiles[app][index] = p;
          if (args.activate) {
            active[app] = p.id;
            accountMode[app] = false;
          }
          return snapshot(app);
        }
        if (command === "activate_profile") {
          active[app] = args.id;
          accountMode[app] = false;
          return snapshot(app);
        }
        if (command === "switch_to_account") {
          if ((window as any).failAccountSwitch)
            throw "配置文件写入失败，已恢复原配置。";
          active[app] = null;
          accountMode[app] = true;
          return snapshot(app);
        }
        if (command === "delete_profile") {
          profiles[app] = profiles[app].filter((p) => p.id !== args.id);
          if (active[app] === args.id) active[app] = null;
          return snapshot(app);
        }
        if (command === "fetch_models") {
          if (args.apiKey === "invalid-key") throw "模型接口返回 HTTP 401。";
          return ["gpt-example", "gpt-example-mini"];
        }
        if (command === "preview_config")
          return JSON.stringify(
            {
              env: {
                ANTHROPIC_BASE_URL: args.profile.baseUrl,
                ANTHROPIC_AUTH_TOKEN: args.profile.apiKey,
              },
              model: args.profile.model,
              extra: true,
            },
            null,
            2,
          );
        if (command === "parse_config") {
          let data;
          try {
            data = JSON.parse(args.source);
          } catch {
            throw "JSON 无效（第 1 行）。";
          }
          return {
            ...args.profile,
            baseUrl: data.env.ANTHROPIC_BASE_URL,
            apiKey: data.env.ANTHROPIC_AUTH_TOKEN,
            model: data.model,
            configText: args.source,
          };
        }
        if (command === "import_profile") {
          if (!profiles[app].some((p) => p.id === "imported"))
            profiles[app].push({
              id: "imported",
              name: "本机配置",
              baseUrl: "https://import.example",
              apiKey: "test-import-key",
              model: "claude-sonnet-4-6",
              notes: "",
              configText: null,
            });
          return snapshot(app);
        }
        return 1;
      },
    };
  }, [topupEnabled, topupUnsupported]);
  await page.goto("/");
  await expect(page.getByRole("article", { name: "日常开发" })).toBeVisible();
}
test.beforeEach(async ({ page }, testInfo) => {
  await mockDesktop(
    page,
    testInfo.title.startsWith("available top-up"),
    testInfo.title.startsWith("unsupported top-up"),
  );
});

test("account login and API configurations switch independently for all three apps", async ({
  page,
}) => {
  for (const name of ["Codex", "Claude Code", "Claude Desktop"]) {
    await page.getByRole("button", { name, exact: true }).click();
    if (name !== "Codex") {
      await page.getByRole("button", { name: "从本机导入" }).click();
      await page
        .getByRole("article", { name: "本机配置" })
        .getByRole("button", { name: "启用", exact: true })
        .click();
    }
    const count = await page.getByRole("article").count();
    await page.getByRole("button", { name: "切换到账号登录" }).click();
    await expect(
      page.getByRole("button", { name: "已选择账号登录" }),
    ).toBeDisabled();
    await expect(page.getByRole("article")).toHaveCount(count);
    await expect(
      page.getByRole("article").getByText("当前使用", { exact: true }),
    ).toHaveCount(0);
    await page
      .getByRole("article")
      .first()
      .getByRole("button", { name: "启用", exact: true })
      .click();
    await expect(
      page.getByRole("button", { name: "切换到账号登录" }),
    ).toBeEnabled();
    await expect(
      page.getByRole("article").getByText("当前使用", { exact: true }),
    ).toHaveCount(1);
  }
});

test("failed account switch keeps the previous active provider", async ({
  page,
}) => {
  await page.evaluate(() => {
    (window as any).failAccountSwitch = true;
  });
  await page.getByRole("button", { name: "切换到账号登录" }).click();
  await expect(page.getByRole("alert")).toContainText("已恢复原配置");
  await expect(
    page
      .getByRole("article", { name: "日常开发" })
      .getByText("当前使用", { exact: true }),
  ).toBeVisible();
  await page.evaluate(() => {
    (window as any).failAccountSwitch = false;
  });
  await page.getByRole("button", { name: "切换到账号登录" }).click();
  await expect(
    page.getByRole("button", { name: "已选择账号登录" }),
  ).toBeDisabled();
});

test("three app configurations, tool navigation, card list and themes", async ({
  page,
}) => {
  await expect(
    page.getByRole("navigation", { name: "应用配置" }).getByRole("button"),
  ).toHaveCount(3);
  await expect(page.getByRole("button", { name: "终端" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Skills 管理" })).toBeVisible();
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await expect(page.getByText("test-daily-key")).toHaveCount(0);
  await expect(page.getByText("自定义提供商", { exact: true })).toHaveCount(0);
  await expect(page.getByText("PROVIDERS", { exact: true })).toHaveCount(0);
  await page.screenshot({ path: "artifacts/ui/providers-light.png" });
  await page.getByRole("button", { name: "切换深色主题" }).click();
  await expect(page.locator("html")).toHaveClass("dark");
  await page.screenshot({ path: "artifacts/ui/providers-dark.png" });
  await page.setViewportSize({ width: 760, height: 580 });
  await page.getByRole("button", { name: "收起侧栏" }).click();
  await expect(
    page.getByRole("button", { name: "添加提供商", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  await page.screenshot({ path: "artifacts/ui/providers-compact.png" });
});

test("skills search results install into the selected client", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Skills 管理" }).click();
  await page.getByLabel("搜索 Skills").fill("sample skill");
  await page.getByRole("button", { name: "搜索", exact: true }).click();
  await expect(page.getByRole("heading", { name: "sample" })).toBeVisible();
  await page.getByRole("button", { name: "安装", exact: true }).click();
  await expect(page.getByText("已安装「sample」到 Codex。")).toBeVisible();
  await expect(
    page.locator(".installed-skills").getByText("sample", { exact: true }),
  ).toHaveCount(1);
});

test("the optional editor installs only on request and opens a window or a project", async ({ page }) => {
  await page.getByRole("button", { name: "内置 VS Code" }).click();
  const commands = () => page.evaluate(() => (window as any).testCalls.filter(
    (call: any) => ["install_workspace", "open_workspace", "launch_client"].includes(call.command)
  ));
  await expect(page.getByText(/包含 Codex、Claude Code、Git 版本控制和简体中文语言包/)).toBeVisible();
  expect(await commands()).toEqual([]);
  await page.getByRole("button", { name: "安装 VS Code" }).click();
  await page.getByRole("button", { name: "打开 VS Code" }).click();
  await page.getByRole("button", { name: "打开项目" }).click();
  const recent = page.getByRole("region", { name: "最近打开的项目" });
  await expect(recent.getByRole("button").first()).toContainText("project with spaces");
  await recent.getByRole("button", { name: /已有项目/ }).click();
  await expect(recent.getByRole("button").first()).toContainText("已有项目");
  expect(await commands()).toMatchObject([
    { command: "install_workspace" },
    { command: "open_workspace", args: { project: null } },
    { command: "open_workspace", args: { project: "C:\\project with spaces" } },
    { command: "open_workspace", args: { project: "C:\\已有项目" } },
  ]);
});

test("desktop actions follow the selected page and local app state", async ({
  page,
}) => {
  const header = page.locator(".page-header");
  const button = (name: string) =>
    header.getByRole("button", { name, exact: true });
  await expect(button("重启 VSCode")).toBeEnabled();
  await button("打开 ChatGPT").click();
  await button("重启 ChatGPT").click();
  expect(
    await page.evaluate(() =>
      (window as any).testCalls
        .filter((entry: any) => entry.command === "launch_client")
        .map((entry: any) => entry.args),
    ),
  ).toEqual([
    { client: "chatgpt", restart: false },
    { client: "chatgpt", restart: true },
  ]);
  await page.evaluate(() => {
    (window as any).runningClients.delete("chatgpt");
    window.dispatchEvent(new Event("focus"));
  });
  await expect(button("打开 ChatGPT")).toBeEnabled();
  await page.getByRole("button", { name: "Claude Code", exact: true }).click();
  await expect(button("重启 VSCode")).toBeEnabled();
  await expect(header.getByText("ChatGPT", { exact: false })).toHaveCount(0);
  await page
    .getByRole("button", { name: "Claude Desktop", exact: true })
    .click();
  await expect(header.getByText("VSCode", { exact: false })).toHaveCount(0);
  await page.evaluate(() => {
    (window as any).launchError = "未找到 Claude";
  });
  await button("打开 Claude").click();
  await expect(page.getByRole("alert")).toContainText("未找到 Claude");
  await page.evaluate(() => {
    (window as any).launchError = "";
  });
  await button("打开 Claude").click();
  await expect(button("重启 Claude")).toBeEnabled();
});

test("terminal fills available width and resizes the active PTY", async ({
  page,
}) => {
  await page.setViewportSize({ width: 960, height: 760 });
  await page.getByRole("button", { name: "终端", exact: true }).click();
  const columns = () =>
    page.evaluate(
      () =>
        (window as any).testCalls
          .filter((entry: any) => entry.command === "terminal_resize")
          .at(-1)?.args.columns ?? 0,
    );
  await expect.poll(columns).toBeGreaterThan(0);
  const narrow = await columns();
  async function expectFullWidth() {
    const gap = await page
      .locator(".terminal-workspace")
      .evaluate((element) => {
        const padding = parseFloat(
          getComputedStyle(element.parentElement!).paddingRight,
        );
        return innerWidth - element.getBoundingClientRect().right - padding;
      });
    expect(Math.abs(gap)).toBeLessThan(2);
  }
  await expectFullWidth();
  await page.setViewportSize({ width: 1464, height: 804 });
  await expect.poll(columns).toBeGreaterThan(narrow + 40);
  await expectFullWidth();
  const wide = await columns();
  const path = await page.getByLabel("终端工作目录").boundingBox();
  const actions = await page.getByRole("group", { name: "终端快捷操作" }).boundingBox();
  expect(actions!.y).toBeGreaterThanOrEqual(path!.y + path!.height);
  await page.getByRole("button", { name: "收起侧栏" }).click();
  await expect.poll(columns).toBeGreaterThan(wide);
  await page.setViewportSize({ width: 760, height: 580 });
  await expect.poll(columns).toBeLessThan(wide);
  await expectFullWidth();
});

test("CLI updates start in their own terminal tabs", async ({ page }) => {
  await page.getByRole("button", { name: "终端", exact: true }).click();
  await expect(page.getByRole("tab", { name: "终端 1" })).toBeVisible();
  for (const [name, command] of [["Codex", "codex update"], ["Claude Code", "claude update"]]) {
    await page.getByRole("button", { name: `更新 ${name}`, exact: true }).click();
    await expect(page.getByRole("tab", { name: `更新 ${name}` })).toHaveAttribute("aria-selected", "true");
    const calls = await page.evaluate(() => (window as any).testCalls);
    const input = calls.filter((c: any) => c.command === "terminal_input").at(-1);
    expect(input.args.data).toBe(command + "\r");
    await expect.poll(() => page.evaluate(() => (window as any).testCalls
      .filter((c: any) => c.command === "terminal_resize").at(-1)?.args.id)).toBe(input.args.id);
  }
  await expect(page.getByRole("tab", { name: "终端 1" })).toBeVisible();
});

test("the rolling release is checked automatically and installs only after confirmation", async ({ page }) => {
  await page.addInitScript(() => { (window as any).updateBuild = 42; });
  await page.reload();
  const entry = page.getByRole("button", { name: "版本更新，发现新版" });
  await expect(entry).toBeVisible();
  await page.getByRole("button", { name: "收起侧栏" }).click();
  await entry.click();
  await expect(page.getByRole("dialog")).toContainText("#42");
  await page.getByRole("button", { name: "稍后" }).click();
  const installs = () => page.evaluate(() => (window as any).testCalls
    .filter((c: any) => c.command === "install_update").map((c: any) => c.args.build));
  expect(await installs()).toEqual([]);
  await entry.click();
  await page.evaluate(() => { (window as any).installError = "更新包完整性校验失败"; });
  await page.getByRole("button", { name: "更新并重启" }).click();
  await expect(page.getByRole("alert")).toContainText("完整性校验失败");
  await page.evaluate(() => { (window as any).installError = null; });
  await page.getByRole("button", { name: "重新检查" }).click();
  await page.getByRole("button", { name: "更新并重启" }).click();
  await expect(page.getByRole("dialog")).toContainText("正在重启");
  expect(await installs()).toEqual([42, 42]);
});

test("terminal navigation opens multiple sessions and preserves them when switching tools", async ({
  page,
}) => {
  await page.getByRole("button", { name: "终端" }).click();
  await expect(page.getByRole("tab", { name: "终端 1" })).toBeVisible();
  await page.getByRole("button", { name: "新终端" }).click();
  await expect(page.getByRole("tab", { name: "终端 2" })).toBeVisible();
  await page.getByRole("button", { name: "关闭终端 1" }).click();
  await page.getByRole("button", { name: "新终端" }).click();
  await expect(page.getByRole("tab", { name: "终端 3" })).toBeVisible();
  await page.getByRole("button", { name: "Skills 管理" }).click();
  await page.getByRole("button", { name: "终端" }).click();
  await expect(page.getByRole("tab", { name: "终端 2" })).toBeVisible();
  await expect(page.getByRole("tab", { name: "终端 3" })).toBeVisible();
});

test("save is separate from activation, apps have independent lists", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Claude Code", exact: true }).click();
  await page.getByRole("button", { name: "添加提供商", exact: true }).click();
  await page.getByLabel("配置名称").fill("我的 Claude");
  await page.getByLabel("Base URL").fill("https://claude.example.com");
  await page.getByLabel("API Key", { exact: true }).fill("test-claude-key");
  await page.getByLabel("模型名称").fill("claude-sonnet-4-6");
  await page.screenshot({ path: "artifacts/ui/provider-editor.png" });
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const card = page.getByRole("article", { name: "我的 Claude" });
  await expect(
    card.getByRole("button", { name: "启用", exact: true }),
  ).toBeEnabled();
  await expect(card).toContainText("账号余额");
  await expect(card).toContainText("42.5 USD");
  await card.getByRole("button", { name: "启用", exact: true }).click();
  await expect(card.getByText("当前使用")).toBeVisible();
  await page.getByRole("button", { name: "Codex", exact: true }).click();
  await expect(page.getByRole("article")).toHaveCount(3);
  const calls = await page.evaluate(() => (window as any).testCalls);
  expect(
    calls.find((c: any) => c.command === "save_profile").args.activate,
  ).toBe(false);
  expect(
    calls.find((c: any) => c.command === "activate_profile").args.app,
  ).toBe("claude");
});

test("edit, copy, search and delete active provider with confirmation", async ({
  page,
}) => {
  await page.getByRole("button", { name: "编辑 日常开发" }).click();
  await expect(page.getByLabel("API Key", { exact: true })).toHaveAttribute(
    "type",
    "password",
  );
  await page.getByRole("button", { name: "显示 API Key", exact: true }).click();
  await expect(page.getByLabel("API Key", { exact: true })).toHaveValue(
    "test-daily-key",
  );
  await page.getByRole("button", { name: "取消", exact: true }).click();
  await page.getByRole("button", { name: "复制 备用服务" }).click();
  await expect(page.getByLabel("配置名称")).toHaveValue("备用服务 副本");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await page.getByLabel("搜索提供商").fill("副本");
  await expect(page.getByRole("article")).toHaveCount(1);
  await page.getByRole("button", { name: "清空搜索" }).click();
  await page.getByRole("button", { name: "删除 日常开发" }).click();
  await expect(page.getByRole("dialog")).toContainText("配置文件会保留");
  await page.getByRole("button", { name: "取消", exact: true }).click();
  await expect(page.getByRole("article", { name: "日常开发" })).toBeVisible();
  await page.getByRole("button", { name: "删除 日常开发" }).click();
  await page.getByRole("button", { name: "删除配置", exact: true }).click();
  await expect(page.getByRole("article", { name: "日常开发" })).toHaveCount(0);
});

test("discard protects unsaved changes and source validation keeps the editor open", async ({
  page,
}) => {
  await page.getByRole("button", { name: "编辑 日常开发" }).click();
  await page.getByLabel("配置名称").fill("未保存");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog").last()).toContainText(
    "放弃未保存的修改",
  );
  await page
    .getByRole("dialog")
    .last()
    .getByRole("button", { name: "取消", exact: true })
    .click();
  await expect(page.getByLabel("配置名称")).toHaveValue("未保存");
  await page.getByRole("tab", { name: "配置源码" }).click();
  await page.getByLabel("配置源码", { exact: true }).fill("{invalid");
  await page.getByRole("button", { name: "保存并启用", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("JSON 无效");
  await expect(page.getByLabel("配置源码", { exact: true })).toHaveValue(
    "{invalid",
  );
  await page.getByRole("button", { name: "关闭编辑器" }).click();
  await page.getByRole("button", { name: "放弃修改" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("model errors retain inputs and valid source synchronizes back to basic form", async ({
  page,
}) => {
  await page.getByRole("button", { name: "编辑 日常开发" }).click();
  await page.getByLabel("API Key", { exact: true }).fill("invalid-key");
  await page.getByRole("button", { name: "获取模型列表" }).click();
  await expect(page.getByRole("alert")).toContainText("401");
  await expect(page.getByLabel("Base URL")).toHaveValue(
    "https://api.example.com/v1",
  );
  await page.getByLabel("API Key", { exact: true }).fill("valid-test-key");
  await page.getByRole("button", { name: "获取模型列表" }).click();
  await expect(page.getByRole("status")).toContainText("2 个模型");
  await page.getByLabel("模型名称").hover();
  await expect(page.getByText("提供商类型", { exact: true })).toHaveCount(0);
  await expect(page.getByText("自定义", { exact: true })).toHaveCount(0);
  await expect(page.locator("#model + svg")).toHaveCount(0);
  await page.screenshot({ path: "artifacts/ui/model-input-hover.png" });
  await page.getByRole("tab", { name: "配置源码" }).click();
  await page.getByLabel("配置源码", { exact: true }).fill(
    JSON.stringify({
      env: {
        ANTHROPIC_BASE_URL: "https://source.example",
        ANTHROPIC_AUTH_TOKEN: "source-test-key",
      },
      model: "source-model",
      extra: true,
    }),
  );
  await page.getByRole("tab", { name: "基础配置" }).click();
  await expect(page.getByLabel("Base URL")).toHaveValue(
    "https://source.example",
  );
  await expect(page.getByLabel("模型名称")).toHaveValue("source-model");
  await page.getByRole("button", { name: "保存并启用" }).click();
  const call = await page.evaluate(() =>
    (window as any).testCalls.findLast(
      (c: any) => c.command === "save_profile",
    ),
  );
  expect(call.args.profile.configText).toContain('"extra": true');
});

test("desktop offers custom direct configuration and optional model discovery", async ({
  page,
}) => {
  await page
    .getByRole("button", { name: "Claude Desktop", exact: true })
    .click();
  await page.getByRole("button", { name: "添加提供商", exact: true }).click();
  await page.getByLabel("配置名称").fill("桌面服务");
  await page.getByLabel("Base URL").fill("https://desktop.example");
  await page.getByLabel("API Key", { exact: true }).fill("test-desktop-key");
  await expect(page.getByLabel("模型名称")).not.toHaveAttribute("required");
  await expect(page.getByRole("dialog")).not.toContainText(
    /映射|路由|Skills|MCP/,
  );
  await page.getByRole("button", { name: "保存并启用" }).click();
  await expect(page.getByRole("article", { name: "桌面服务" })).toContainText(
    "当前使用",
  );
  await page.getByRole("button", { name: "从本机导入" }).click();
  await expect(page.getByRole("article", { name: "本机配置" })).toBeVisible();
});

test("balance defaults to auto, custom query can be tested and persisted without activation", async ({
  page,
}) => {
  await expect(page.getByRole("button", { name: "充值 日常开发" })).toHaveCount(
    0,
  );
  await expect(page.getByRole("article", { name: "日常开发" })).toContainText(
    "42.5 USD",
  );
  await page.getByRole("button", { name: "余额配置 日常开发" }).click();
  await expect(page.getByLabel("余额查询方式")).toHaveValue("auto");
  await page.getByLabel("余额查询方式").selectOption("custom");
  await page.getByLabel("查询 Base URL").fill("https://billing.example");
  await page.getByLabel("Access Token").fill("private-balance-token");
  await page.getByLabel("自动刷新间隔（分钟）").fill("10");
  await page
    .getByLabel("查询脚本")
    .fill(
      '({request:{url:"{{baseUrl}}/balance",headers:{Authorization:"Bearer {{accessToken}}"}},extractor:r=>({remaining:r.balance,unit:"USD"})})',
    );
  await page.getByRole("button", { name: "测试余额查询" }).click();
  await expect(page.getByRole("dialog").getByRole("status")).toContainText(
    "自定义余额",
  );
  await expect(page.getByRole("dialog").getByRole("status")).toContainText(
    "42.5 USD",
  );
  await page.screenshot({ path: "artifacts/ui/balance-settings.png" });
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(page.getByRole("article", { name: "日常开发" })).toContainText(
    "自定义余额",
  );
  const save = await page.evaluate(() =>
    (window as any).testCalls.findLast(
      (c: any) => c.command === "save_profile",
    ),
  );
  expect(save.args.activate).toBe(false);
  expect(save.args.profile.balance.intervalMinutes).toBe(10);
  expect(save.args.profile.balance.accessToken).toBe("private-balance-token");
  await page.getByRole("button", { name: "余额配置 日常开发" }).click();
  await expect(page.getByLabel("查询 Base URL")).toHaveValue(
    "https://billing.example",
  );
  await expect(page.getByLabel("查询脚本")).toContainText("extractor");
});

test("available top-up creates an order and displays its payment QR code", async ({
  page,
}) => {
  await page.getByRole("button", { name: "充值 日常开发" }).click();
  await page.getByLabel("充值金额").fill("10");
  await page.getByRole("button", { name: "确定充值" }).click();
  await expect(page.getByText("订单已创建 · 应付金额 73.00")).toBeVisible();
  await expect(page.locator(".checkout-result svg")).toBeVisible();
  const topup = await page.evaluate(() =>
    (window as any).testCalls.findLast(
      (call: any) => call.command === "create_topup",
    ),
  );
  expect(topup.args.amount).toBe(10);
  expect(topup.args.paymentMethod).toBe("alipay");
});

test("unsupported top-up endpoints hide the recharge button", async ({
  page,
}) => {
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).testCalls.filter(
            (call: any) => call.command === "fetch_topup_info",
          ).length,
      ),
    )
    .toBeGreaterThan(0);
  await expect(
    page.getByRole("button", { name: "充值 日常开发" }),
  ).toHaveCount(0);
});

test("balance errors stay local to a card; off and manual-only settings control requests", async ({
  page,
}) => {
  await page.evaluate(() => {
    (window as any).failBalance = true;
  });
  await page.getByRole("button", { name: "刷新余额 日常开发" }).click();
  await expect(page.getByRole("article", { name: "日常开发" })).toContainText(
    "站点不支持此余额接口",
  );
  await expect(page.getByRole("article", { name: "备用服务" })).toContainText(
    "42.5 USD",
  );
  await page.getByRole("button", { name: "余额配置 日常开发" }).click();
  await page.getByLabel("余额查询方式").selectOption("off");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "刷新余额 日常开发" }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "余额配置 日常开发" }).click();
  await page.getByLabel("余额查询方式").selectOption("auto");
  await page.getByLabel("自动刷新间隔（分钟）").fill("0");
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(page.getByRole("article", { name: "日常开发" })).toContainText(
    "余额待查询",
  );
  await page.evaluate(() => {
    (window as any).failBalance = false;
  });
  await page.getByRole("button", { name: "刷新余额 日常开发" }).click();
  await expect(page.getByRole("article", { name: "日常开发" })).toContainText(
    "42.5 USD",
  );
});
