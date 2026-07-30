const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

test("login route has no serious WCAG accessibility violations", async ({ page }) => {
  await page.goto("/login");
  await page.locator("main").waitFor();
  await page.locator("button[type='button'], button").first().waitFor({ state: "visible" });
  await page.waitForTimeout(250);

  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  const blocking = results.violations.filter(
    violation => violation.impact === "critical" || violation.impact === "serious"
  );

  expect(blocking, JSON.stringify(blocking, null, 2)).toEqual([]);
});

test("authenticated dashboard is responsive and has no serious WCAG violations", async ({ page }) => {
  const now = new Date().toISOString();
  await page.route("**/api/**", async route => {
    const path = new URL(route.request().url()).pathname;
    const bodies = {
      "/api/auth/me": { username: "operator", role: "admin" },
      "/api/builds/stats": { success: true, data: { totalCount: 42, successfulCount: 38, failedCount: 4 } },
      "/api/builds/queue": { success: true, data: { queued: [], running: [], queuedCount: 0, runningCount: 0 } },
      "/api/builds": { success: true, data: { builds: [{ id: "018f1670-dff0-7000-8000-000000000001", pipelineId: "018f1670-dff0-7000-8000-000000000002", status: "Success", specName: "lumina-agent.spec", createdAt: now, triggeredBy: "operator" }], totalCount: 1, page: 1, pageSize: 20 } },
      "/api/pipelines": { success: true, data: { pipelines: [{ id: "018f1670-dff0-7000-8000-000000000002", name: "Release packages", description: "Build, verify, and publish the stable channel.", status: "Active", createdBy: "operator", createdAt: now, stepCount: 4 }], totalCount: 1, page: 1, pageSize: 20 } },
      "/api/scanner/scans": { success: true, data: { scans: [], totalCount: 0, page: 1, pageSize: 20 } }
    };
    const body = bodies[path];
    await route.fulfill(body ? { status: 200, contentType: "application/json", body: JSON.stringify(body) } : { status: 404, body: "{}" });
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Delivery pulse" })).toBeVisible();
  await expect(page.getByText("Release packages")).toBeVisible();

  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  const blocking = results.violations.filter(
    violation => violation.impact === "critical" || violation.impact === "serious"
  );
  expect(blocking, JSON.stringify(blocking, null, 2)).toEqual([]);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole("button", { name: "Open navigation" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Delivery pulse" })).toBeVisible();
});
