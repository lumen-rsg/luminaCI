const { defineConfig, devices } = require("@playwright/test");

module.exports = defineConfig({
  testDir: ".",
  testMatch: "*.spec.js",
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: "http://127.0.0.1:5188",
    trace: "retain-on-failure",
    ...devices["Desktop Chrome"]
  },
  webServer: {
    command: "npm run dev -- --host 127.0.0.1",
    cwd: "../../src/Services/Lumina.WebApp",
    url: "http://127.0.0.1:5188/login",
    reuseExistingServer: !process.env.CI,
    timeout: 120000
  }
});
