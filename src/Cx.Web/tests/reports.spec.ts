import { test, expect } from "@playwright/test";

const metrics = {
  total: 4,
  customers: 2,
  analyzed: 3,
  negative: 1,
  complaints: 1,
  open_high_severity: 1,
  csat_responses: 2,
  csat_satisfied: 1,
  csat_percent: 50,
  ces_responses: 2,
  ces_average: 4,
  ces_easy_percent: 50,
  nps_responses: 1,
  nps_score: 100,
};
const group = (name: string) => ({ name, ...metrics });
const report = {
  from: "2026-10-01T00:00:00+00:00",
  to: "2026-10-12T00:00:00+00:00",
  summary: metrics,
  csat_distribution: Array.from({ length: 5 }, (_, i) => ({
    name: String(i + 1),
    count: i === 1 || i === 3 ? 1 : 0,
  })),
  ces_distribution: Array.from({ length: 7 }, (_, i) => ({
    name: String(i + 1),
    count: i === 0 || i === 6 ? 1 : 0,
  })),
  trend: [group("2026-10-01"), group("2026-10-02")],
  topics: [group("delivery")],
  sentiments: [group("negative")],
  channels: [group("web")],
  severities: [group("high")],
  types: [group("voc")],
  statuses: [group("new")],
  available_channels: ["web", "phone"],
  available_topics: ["delivery"],
};
const topic = (id: string, title: string, parent_id: string | null) => ({
  id, code: id, title, parent_id, is_active: true, sort_order: 0, keywords: [],
});
test.beforeEach(async ({ page }) => {
  await page.route("**/v1/**", (r) => r.fulfill({ status: 404, json: {} }));
  await page.route("**/v1/auth/me", (r) =>
    r.fulfill({
      json: {
        username: "admin",
        display_name: "Manager",
        tenant_id: "bestcx",
        mode: "login",
        permissions: ["cx.admin"],
      },
    }),
  );
  await page.route("**/v1/admin/topics*", (r) =>
    r.fulfill({ json: { revision: 1, topics: [
      topic("delivery", "Sending", null),
      topic("delivery.delay", "Delay", "delivery"),
      topic("delivery.delay.post", "Post", "delivery.delay"),
      topic("payment", "Payment", null),
      topic("payment.error", "Payment error", "payment"),
      topic("empty", "No subcategory", null),
    ] } }),
  );
  await page.route("**/v1/reports?*", (r) => r.fulfill({ json: report }));
  await page.route("**/v1/customers?*", r => r.fulfill({ json: { items: [], has_more: false, next_cursor: null } }));
});

test("report filters combine and include the end day in UTC; CSV exports the applied report", async ({
  page,
}, testInfo) => {
  const requests: URL[] = [];
  await page.route("**/v1/reports?*", (r) => {
    requests.push(new URL(r.request().url()));
    return r.fulfill({ json: report });
  });
  await page.goto("/reports");
  await page.locator(".report-more-filters > summary").click();
  await page.getByRole("button", { name: "Customer scores", exact: true }).click();
  await page.getByRole("button", { name: "Customer scores", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Satisfaction · CSAT" })).toBeVisible();
  const panel = page.locator(".report-filters");

  const controls = await panel.locator("select, .calendar-date-field input, .feedback-search-row > .grid-filter > input, .picker-input")
    .evaluateAll(elements => elements.filter(element => element.getClientRects().length).map(element => element.getBoundingClientRect().height));
  expect(controls.every(height => height >= 36)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("report-filters-desktop.png") });
  await page.getByLabel("Start date", { exact: true }).fill("2026/10/01");
  await page.getByLabel("End date", { exact: true }).fill("2026/10/11");
  for (const [title, value] of [
    ["Main category", "delivery"],
    ["Sentiment", "negative"],
    ["Channel", "web"],
    ["Registration type", "voc"],
  ]) {
    await page.getByRole("combobox", { name: title, exact: true }).selectOption(value);
  }
  await expect(page.locator('.report-filters input[type="checkbox"]')).toHaveCount(0);
  await page.getByLabel("Search in text").fill("Sending delay");
  await page.getByLabel("Time grouping").selectOption("week");
  await page.getByRole("button", { name: "Display report" }).click();
  await expect
    .poll(() => requests.at(-1)?.searchParams.get("topics"))
    .toBe("delivery");
  const p = requests.at(-1)!.searchParams;
  expect(p.get("from")).toBe("2026-10-01T00:00:00.000Z");
  expect(p.get("to")).toBe("2026-10-12T00:00:00.000Z");
  expect(p.get("sentiments")).toBe("negative");
  expect(p.get("channels")).toBe("web");
  expect(p.get("severities")).toBeNull();
  expect(p.get("types")).toBe("voc");
  expect(p.get("statuses")).toBeNull();
  expect(p.get("q")).toBe("Sending delay");
  expect(p.get("bucket")).toBe("week");
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Receive CSV" }).click();
  expect((await download).suggestedFilename()).toMatch(/^cx-feedback-report-.*\.csv$/);
  await page.getByRole("button", { name: "Clear filters" }).click();
  await expect
    .poll(() => requests.at(-1)?.searchParams.get("topics"))
    .toBeNull();
});

test("report topic filters cascade, restore descendants from the URL, and clear a previous branch", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/reports?*", r => {
    requests.push(new URL(r.request().url()).searchParams);
    return r.fulfill({ json: report });
  });
  await page.goto("/reports");
  await page.locator(".report-more-filters > summary").click();
  const parent = page.getByRole("combobox", { name: "Main category", exact: true });
  const child = page.getByRole("combobox", { name: "Subcategory", exact: true });
  await expect(child).toBeDisabled();
  await parent.selectOption("delivery");
  await expect(child.locator("option")).toHaveText(["All subcategories", "Delay", "Delay › Post"]);
  await child.selectOption("delivery.delay.post");
  await page.getByRole("button", { name: "Display report" }).click();
  await expect.poll(() => requests.at(-1)?.get("topics")).toBe("delivery.delay.post");
  await page.reload();
  await page.locator(".report-more-filters > summary").click();
  await expect(parent).toHaveValue("delivery");
  await expect(child).toHaveValue("delivery.delay.post");
  await parent.selectOption("payment");
  await expect(child).toHaveValue("");
  await expect(child.locator("option")).toHaveText(["All subcategories", "Payment error"]);
  await page.getByRole("button", { name: "Display report" }).click();
  await expect.poll(() => requests.at(-1)?.get("topics")).toBe("payment");
  await parent.selectOption("empty");
  await expect(child).toBeDisabled();
  await expect(child).toHaveValue("");
  await page.getByRole("button", { name: "Clear filters" }).click();
  await expect(parent).toHaveValue("");
  await expect(child).toBeDisabled();
  await expect.poll(() => requests.at(-1)?.get("topics")).toBeNull();
});

test("old multi-value report URLs remain visible and can be replaced with a single dropdown selection", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/reports?*", r => {
    requests.push(new URL(r.request().url()).searchParams);
    return r.fulfill({ json: report });
  });
  await page.goto("/reports?topics=delivery,payment&sentiments=negative,positive");
  await page.locator(".report-more-filters > summary").click();
  const parent = page.getByRole("combobox", { name: "Main category", exact: true });
  const sentiment = page.getByRole("combobox", { name: "Sentiment", exact: true });
  await expect(parent.locator("option:checked")).toHaveText("Sending, Payment");
  await expect(sentiment.locator("option:checked")).toHaveText("Negative, Positive");
  await parent.selectOption("delivery");
  await sentiment.selectOption("negative");
  await page.getByRole("button", { name: "Display report" }).click();
  await expect.poll(() => requests.at(-1)?.get("topics")).toBe("delivery");
  await expect.poll(() => requests.at(-1)?.get("sentiments")).toBe("negative");
});

test("reports search customers, combine the customer filter, restore it, and clear it", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  const customerSearches: string[] = [];
  await page.route("**/v1/customers?*", r => {
    customerSearches.push(new URL(r.request().url()).searchParams.get("q") ?? "");
    return r.fulfill({ json: {
      items: [{ customer_id: "C-SECOND", display_name: "Maryam Ahmadi" }], has_more: false, next_cursor: null,
    } });
  });
  await page.route("**/v1/customers/C-SECOND", r => r.fulfill({ json: { customer: { customer_id: "C-SECOND", display_name: "Maryam Ahmadi" } } }));
  await page.route("**/v1/reports?*", r => {
    requests.push(new URL(r.request().url()).searchParams);
    return r.fulfill({ json: report });
  });
  await page.goto("/reports?channels=web");
  await page.locator(".report-more-filters > summary").click();
  const picker = page.getByRole("combobox", { name: "Customer search", exact: true });
  await picker.fill("Maryam");
  await expect(page.getByRole("option", { name: /Maryam Ahmadi/ })).toBeVisible();
  expect(customerSearches).toContain("Maryam");
  await picker.press("Enter");
  await page.getByRole("button", { name: "Display report" }).click();
  await expect.poll(() => requests.at(-1)?.get("customerId")).toBe("C-SECOND");
  expect(requests.at(-1)?.get("channels")).toBe("web");
  await page.reload();
  await page.locator(".report-more-filters > summary").click();
  await expect(picker).toHaveValue("Maryam Ahmadi");
  await page.getByRole("button", { name: "Clear filters" }).click();
  await expect(picker).toHaveValue("");
  await expect.poll(() => requests.at(-1)?.get("customerId")).toBeNull();
});

test("empty responses remain missing and report stays within a mobile viewport", async ({
  page,
}, testInfo) => {
  await page.route("**/v1/reports?*", (r) =>
    r.fulfill({
      json: {
        ...report,
        summary: {
          ...metrics,
          total: 0,
          csat_responses: 0,
          csat_percent: null,
          ces_responses: 0,
          ces_average: null,
        },
        trend: [],
        topics: [],
        channels: [],
        sentiments: [],
        severities: [],
        types: [],
        statuses: [],
      },
    }),
  );
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/reports");
  await page.locator(".report-more-filters > summary").click();
  await expect(page.getByText("In this period and with these filters, no case has been registered")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Satisfaction · CSAT" })).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("report-filters-mobile.png"), fullPage: true });
});

test("invalid date ranges do not request a new report", async ({ page }) => {
  let requests = 0;
  await page.route("**/v1/reports?*", (r) => {
    requests++;
    return r.fulfill({ json: report });
  });
  await page.goto("/reports");
  await page.locator(".report-more-filters > summary").click();
  await expect(
    page.getByRole("button", { name: "Display report" }),
  ).toBeEnabled();
  const before = requests;
  await page.getByLabel("Start date", { exact: true }).fill("2026/10/12");
  await page.getByLabel("End date", { exact: true }).fill("2026/10/01");
  await page.getByRole("button", { name: "Display report" }).click();
  await expect(page.getByRole("alert")).toContainText("time period");
  expect(requests).toBe(before);
});

test("reports reject invalid Gregorian leap days and empty required dates, and accept calendar selection", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/reports?*", route => {
    requests.push(new URL(route.request().url()).searchParams);
    return route.fulfill({ json: report });
  });
  await page.goto("/reports?start=2026-10-01&end=2026-10-11");
  await page.locator(".report-more-filters > summary").click();
  await page.getByRole("button", { name: "Customer scores", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Satisfaction · CSAT" })).toBeVisible();
  const before = requests.length;
  await expect(page.getByLabel("Start date", { exact: true })).toHaveValue("2026/10/01");
  await page.getByLabel("Start date", { exact: true }).fill("2025/02/29");
  await page.getByRole("button", { name: "Display report", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("valid date");
  expect(requests.length).toBe(before);
  await page.getByLabel("Start date", { exact: true }).fill("");
  await page.getByRole("button", { name: "Display report", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Select a date");
  expect(requests.length).toBe(before);
  await page.getByRole("button", { name: "Open the calendar Start date", exact: true }).click();
  const calendar = page.getByRole("dialog", { name: "Calendar Start date", exact: true });
  await expect(calendar.getByRole("button", { name: "Clear", exact: true })).toHaveCount(0);
  await calendar.getByLabel("Year Start date", { exact: true }).selectOption("2026");
  await calendar.getByLabel("Month Start date", { exact: true }).selectOption("10");
  await calendar.getByRole("button", { name: "2 October 2026", exact: true }).click();
  await page.getByRole("button", { name: "Display report", exact: true }).click();
  await expect.poll(() => requests.at(-1)?.get("from")).toBe("2026-10-02T00:00:00.000Z");
  await page.getByRole("button", { name: "Clear filters", exact: true }).click();
  const defaultDate = await page.getByLabel("Start date", { exact: true }).inputValue();
  await page.getByLabel("Start date", { exact: true }).fill("2025/02/29");
  await page.getByRole("button", { name: "Clear filters", exact: true }).click();
  await expect(page.getByLabel("Start date", { exact: true })).toHaveValue(defaultDate);
  await expect(page.getByRole("alert")).toHaveCount(0);
});
