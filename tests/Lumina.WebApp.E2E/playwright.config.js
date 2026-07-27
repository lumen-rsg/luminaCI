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
    command: "dotnet run --project ../../src/Services/Lumina.WebApp/Lumina.WebApp.csproj --configuration Release --no-build --no-launch-profile --urls http://127.0.0.1:5188",
    url: "http://127.0.0.1:5188/login",
    reuseExistingServer: !process.env.CI,
    timeout: 120000
  }
});
