import { expect, test } from "@playwright/test";
import { formatCalendarDate, parseCalendarDate, calendarMonthDays, toGregorianDate, toCalendarDate } from "../src/calendarDate";

test("Gregorian dates round-trip, validate leap years, and use English digits", () => {
  for (const iso of ["2024-02-29", "2025-03-20", "2026-10-01", "2026-12-31", "0001-01-01"]) {
    expect(formatCalendarDate(iso)).toBe(iso.replaceAll("-", "/"));
    expect(parseCalendarDate(formatCalendarDate(iso))).toBe(iso);
    expect(toGregorianDate(toCalendarDate(iso))).toBe(iso);
  }
  expect(parseCalendarDate("2026/10/2")).toBe("2026-10-02");
  expect(parseCalendarDate("20261002")).toBe("2026-10-02");
  for (const invalid of ["2025/02/29", "2026/04/31", "2026/13/01", "2026/01/00", "2026/7", "not a date"])
    expect(parseCalendarDate(invalid)).toBeNull();
  expect(calendarMonthDays(2024, 2)).toBe(29);
  expect(calendarMonthDays(2025, 2)).toBe(28);
  expect(calendarMonthDays(2026, 1)).toBe(31);
});

test.describe("English calendar", () => {
  test.use({ timezoneId: "America/Los_Angeles" });
  test.beforeEach(async ({ page }) => {
    await page.route("**/v1/**", route => route.fulfill({ status: 404, json: {} }));
    await page.route("**/v1/auth/me", route => route.fulfill({ json: {
      username: "admin", display_name: "Administrator", tenant_id: "bestcx", mode: "login", permissions: ["cx.admin"],
    } }));
    await page.route("**/v1/admin/topics*", route => route.fulfill({ json: { revision: 1, topics: [] } }));
    await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } }));
  });

  test("selects a day, enforces range bounds, and persists Gregorian values", async ({ page }) => {
    await page.goto("/experience?start=2026-10-01&end=2026-10-11");
    await page.getByRole("button", { name: /^Filters/ }).click();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(page.getByLabel("Start date", { exact: true })).toHaveValue("2026/10/01");
    await page.getByRole("button", { name: "Open the calendar Start date", exact: true }).click();
    const calendar = page.getByRole("dialog", { name: "Calendar Start date", exact: true });
    await expect(calendar.getByLabel("Month Start date", { exact: true })).toHaveValue("10");
    await expect(calendar.getByRole("button", { name: "12 October 2026", exact: true })).toBeDisabled();
    await calendar.getByRole("button", { name: "2 October 2026", exact: true }).click();
    await expect(page).toHaveURL(/start=2026-10-02/);
    await page.reload();
    await page.getByRole("button", { name: /^Filters/ }).click();
    await expect(page.getByLabel("Start date", { exact: true })).toHaveValue("2026/10/02");
    await page.getByRole("button", { name: "Open the calendar End date", exact: true }).click();
    await expect(page.getByRole("dialog").getByRole("button", { name: "1 October 2026", exact: true })).toBeDisabled();
    await page.getByRole("dialog").getByRole("button", { name: "Clear", exact: true }).click();
    await expect(page.getByLabel("End date", { exact: true })).toHaveValue("");
  });

  test("rejects invalid leap dates and supports keyboard dismissal", async ({ page }) => {
    await page.goto("/experience");
    await page.getByRole("button", { name: /^Filters/ }).click();
    const start = page.getByLabel("Start date", { exact: true });
    await start.pressSequentially("2024/02/29");
    await start.press("Tab");
    await expect(page).toHaveURL(/start=2024-02-29/);
    await start.fill("2025/02/29");
    await start.press("Tab");
    await expect(page.getByRole("alert")).toContainText("valid date");
    await expect(page).toHaveURL(/start=2024-02-29/);
    await start.fill("2025/02/28");
    await start.press("Tab");
    await start.press("ArrowDown");
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(start).toBeFocused();
  });

  test("month navigation crosses a year boundary", async ({ page }) => {
    await page.goto("/experience?start=2026-12-31");
    await page.getByRole("button", { name: /^Filters/ }).click();
    await page.getByRole("button", { name: "Open the calendar Start date", exact: true }).click();
    const calendar = page.getByRole("dialog");
    await calendar.getByRole("button", { name: "Next month", exact: true }).click();
    await expect(calendar.getByLabel("Month Start date", { exact: true })).toHaveValue("1");
    await expect(calendar.getByLabel("Year Start date", { exact: true })).toHaveValue("2027");
  });

  for (const theme of ["light", "dark"]) {
    test(`mobile ${theme} calendar stays within the viewport`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.addInitScript(theme => localStorage.setItem("cx-theme", theme), theme);
      await page.goto("/experience?start=2026-10-01");
    await page.getByRole("button", { name: /^Filters/ }).click();
      await page.getByRole("button", { name: "Open the calendar Start date", exact: true }).click();
      const box = (await page.getByRole("dialog").boundingBox())!;
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(390);
      expect(box.y + box.height).toBeLessThanOrEqual(844);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath(`calendar-${theme}.png`) });
    });
  }
});
