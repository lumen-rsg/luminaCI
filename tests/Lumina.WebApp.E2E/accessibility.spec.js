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
