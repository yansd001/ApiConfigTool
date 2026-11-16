import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "tests/e2e",
  fullyParallel: true,
  use: {
    baseURL: "http://127.0.0.1:1420",
    viewport: { width: 1120, height: 760 },
    channel: process.env.CI ? "chromium" : "msedge",
    trace: "retain-on-failure",
  },
  webServer: {
    command: "npm run dev:renderer",
    url: "http://127.0.0.1:1420",
    reuseExistingServer: !process.env.CI,
  },
  reporter: "list",
});
