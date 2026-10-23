import { test, expect } from "@playwright/test";

const sessionUser = {
  username: "admin",
  display_name: "Administrator",
  tenant_id: "bestcx",
  mode: "login",
  permissions: ["cx.admin"],
};

// Every workflow runs as a signed-in operator unless a test says otherwise.
test.beforeEach(async ({ page }) => {
  // Lowest priority (registered first): a call a test did not mock must never reach a
  // real backend, whose answer would make the test depend on the environment.
  await page.route("**/v1/**", (route) => route.fulfill({ status: 404, json: { error: "not mocked" } }));
  await page.route("**/v1/auth/me", (route) => route.fulfill({ json: sessionUser }));
  await page.route("**/v1/customers?*", route => route.fulfill({ json: { items: [], has_more: false, next_cursor: null } }));
  await page.route("**/v1/reports?*", route => route.fulfill({ json: {
    summary: { csat_percent: 75, csat_responses: 100, ces_average: 5.4, ces_responses: 100, ces_easy_percent: 80 },
    csat_distribution: Array.from({ length: 5 }, (_, i) => ({ name: String(i + 1), count: (i + 1) * 4 })),
    ces_distribution: Array.from({ length: 7 }, (_, i) => ({ name: String(i + 1), count: (i + 1) * 3 })),
  } }));
});
const feedback = {
  id: "11111111-1111-1111-1111-111111111111",
  customer_id: "customer-test",
  comment: "Follow up on customer payment issues",
  feedback_type: "comment",
  status: "new",
  reviewed: false,
  channel: "web",
  submitted_at: "2026-09-21T10:00:00Z",
};
const pageOf = (items: unknown[]) => ({
  items,
  has_more: false,
  next_cursor: null,
});

test("admin creates a database-backed customer login from the customer profile", async ({ page }) => {
  await page.route("**/v1/customers/C-TEST0001", route => route.fulfill({ json: {
    customer, open_support_cases: 0, recent_calls: [], recent_feedback: [], latest_analysis: null,
  } }));
  await page.route("**/v1/customers/C-TEST0001/insights", route => route.fulfill({ json: { recurring_issues: [], unresolved_issues: 0 } }));
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([]) }));
  let saved: unknown;
  await page.route("**/v1/customers/C-TEST0001/portal-account", route => {
    if (route.request().method() === "PUT") {
      saved = route.request().postDataJSON();
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: { exists: false, username: null, enabled: true, role: "customer" } });
  });
  await page.goto("/customers/C-TEST0001");
  await page.getByRole("button", { name: "Customer portal account" }).click();
  await page.getByLabel("Username", { exact: true }).fill("mohammad");
  await page.getByLabel("Password", { exact: true }).fill("mohammad123");
  await page.getByRole("button", { name: "Save customer account" }).click();
  await expect(page.getByRole("dialog")).not.toBeVisible();
  expect(saved).toEqual({ username: "mohammad", password: "mohammad123", enabled: true });
});
const customer = {
  customer_id: "C-TEST0001",
  display_name: "Maryam Ahmadi",
  phone: "09123456789",
  feedback_count: 0,
  negative_count: 0,
  first_seen_at: "2026-09-21T10:00:00Z",
  last_seen_at: "2026-09-21T10:00:00Z",
  created_at: "2026-09-21T10:00:00Z",
};
const analysis = {
  id: "analysis-1",
  entity_type: "cx_event",
  entity_id: "event-1",
  analysis_type: "event_analysis",
  model_provider: "openrouter",
  model_name: "google/gemini-3.1-flash-lite",
  status: "succeeded",
  created_at: "2026-09-21T10:00:05Z",
  result: {
    sentiment: "negative",
    intent: "complaint",
    category: "payment",
    severity: "high",
    resolution_status: "unresolved",
    summary: "The customer is unhappy with the payment error.",
    action_items: ["Transaction review"],
    mentioned_products: [],
    confidence: 0.9,
  },
};
const gridRow = {
  id: feedback.id,
  kind: "text",
  customer_id: feedback.customer_id,
  customer_name: "Maryam Ahmadi",
  comment: feedback.comment,
  channel: feedback.channel,
  feedback_type: "generic",
  status: "new",
  reviewed: false,
  submitted_at: feedback.submitted_at,
  analysis_state: "analyzed",
  sentiment: "negative",
  severity: "high",
  category: "payment",
  summary: analysis.result.summary,
  confidence: 0.9,
  human_reviewed: false,
};
const gridOf = (items: unknown[]) => ({
  items,
  total: items.length,
  page: 1,
  page_size: 20,
  status_counts: { new: items.length },
});
const overview = {
  from: "2026-09-14",
  to: "2026-09-21",
  total_interactions: 70,
  active_customers: 30,
  feedback_count: 12,
  nps: { total: 10, promoters: 6, passives: 2, detractors: 2,
    promoter_percent: 60, passive_percent: 20, detractor_percent: 20, score: 40 },
  support_cases: 4,
  call_count: 8,
  negative_sentiment_share: 0.4,
  unresolved_high_severity: 3,
  ai_review_backlog: 5,
  data_quality_warnings: 0,
  top_pain_points: [{ name: "payment", count: 3 }],
  top_affected_products: [],
  sentiment_breakdown: [
    { name: "negative", count: 4 },
    { name: "positive", count: 6 },
  ],
  daily_trend: [
    { date: "2026-09-20", events: 10, negative: 2, calls: 2 },
    { date: "2026-09-21", events: 20, negative: 1, calls: 3 },
  ],
};

test("dashboard uses selected reporting window and has a working mobile menu", async ({
  page,
}) => {
  const windows: string[] = [];
  await page.route("**/v1/overview?*", (route) => {
    windows.push(new URL(route.request().url()).searchParams.get("days")!);
    return route.fulfill({ json: overview });
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Overview",
  );
  await page.getByLabel("Time frame").selectOption("30");
  await expect.poll(() => windows).toContain("30");
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.getByRole("button", { name: "Open the menu" }).click();
  await expect(
    page.getByRole("navigation", { name: "Main navigation" }),
  ).toBeVisible();
  await page
    .getByRole("navigation")
    .getByRole("link", { name: "Customers" })
    .click();
  await expect(page).toHaveURL(/\/customers$/);
  await expect(page.locator(".sidebar")).not.toBeVisible();
});

for (const [width, height] of [[1440, 900], [1366, 768], [1280, 720], [1024, 768], [390, 844]]) {
  test(`overview fits the first viewport at ${width}x${height}`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    await page.route("**/v1/overview?*", route => route.fulfill({ json: {
      ...overview,
      sentiment_breakdown: ["positive", "negative", "neutral", "mixed", "unknown"].map(name => ({ name, count: 10 })),
      top_pain_points: Array.from({ length: 20 }, (_, i) => ({ name: `Frequent topic no ${i + 1}`, count: 20 - i })),
    } }));
    await page.goto("/");
    await expect(page.getByRole("heading", { name: "Effort · CES" })).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    const layout = await page.evaluate(() => ({
      height: document.documentElement.scrollHeight,
      sections: [...document.querySelectorAll('.overview-dashboard > *, .overview-experience, .overview-insights')]
        .map(el => ({ name: el.className, height: el.getBoundingClientRect().height, bottom: el.getBoundingClientRect().bottom })),
    }));
    expect(layout.height, JSON.stringify(layout)).toBeLessThanOrEqual(height);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const section of await page.locator('.overview-dashboard .card, .overview-dashboard .page-footnote').all())
      await expect(section).toBeInViewport({ ratio: 1 });
  });
}

test("feedback URL search survives reload and ignores complaint-only filters", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/feedback/grid?*", route => { requests.push(new URL(route.request().url()).searchParams); return route.fulfill({ json: gridOf([gridRow]) }); });
  await page.route(`**/v1/feedback/${feedback.id}`, route => route.fulfill({ json: { feedback, analysis } }));
  await page.goto("/experience?status=new&severity=critical&q=Payment");
  await expect(page.locator(".grid-search input")).toHaveValue("Payment");
  await page.reload();
  await expect(page.locator(".grid-search input")).toHaveValue("Payment");
  expect(requests.at(-1)?.get("inbox")).toBe("feedback");
  expect(requests.at(-1)?.get("status")).toBeNull();
  expect(requests.at(-1)?.get("severity")).toBeNull();
  await page.locator(".data-grid tbody tr").first().click();
  const drawer = page.getByRole("dialog", { name: "Feedback details" });
  await expect(drawer).toContainText(analysis.result.summary);
  await expect(drawer.getByRole("button", { name: "It was checked", exact: true })).toHaveCount(0);
  await expect(drawer.getByRole("link", { name: "Create case", exact: true })).toHaveCount(0);
});

test("the staff console no longer offers feedback submission", async ({ page }) => {
  await page.route("**/v1/overview?*", route => route.fulfill({ json: overview }));
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Submit feedback" })).toHaveCount(0);
  await page.goto("/collect");
  await expect(page.getByText("Page not found")).toBeVisible();
  await expect(page.getByRole("textbox", { name: "What did the customer say?" })).toHaveCount(0);
});

test("grid filters and numbered pages are sent to the API", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/feedback/grid?*", (route) => {
    const params = new URL(route.request().url()).searchParams;
    requests.push(params);
    return route.fulfill({
      json: { ...gridOf([gridRow]), total: 45, page: Number(params.get("page") ?? 1) },
    });
  });
  await page.goto("/inbox");
  await page.getByRole("button", { name: /^Filters/ }).click();
  await expect(page.getByRole("navigation", { name: "Pagination" })).toContainText("of 45");
  await page.getByLabel("Sentiment").selectOption("negative");
  await expect.poll(() => requests.at(-1)?.get("sentiment")).toBe("negative");
  await page.getByRole("button", { name: "3", exact: true }).click();
  await expect.poll(() => requests.at(-1)?.get("page")).toBe("3");
  await expect(page).toHaveURL(/sentiment=negative/);
  await expect(page).toHaveURL(/page=3/);
  await expect(page.getByRole("columnheader", { name: "Channel", exact: true })).toBeVisible();
  await expect(page.getByRole("cell", { name: "Website", exact: true })).toBeVisible();
  await page.getByLabel("Channel", { exact: true }).selectOption("manual");
  await expect.poll(() => requests.at(-1)?.get("channel")).toBe("manual");
  await expect.poll(() => requests.at(-1)?.get("page")).toBe("1");
  await page.reload();
  if (new URL(page.url()).pathname === "/experience") { await page.locator(".filter-toggle").waitFor(); await page.locator(".filter-toggle").click(); }
  await expect(page.getByLabel("Channel", { exact: true })).toHaveValue("manual");
  await page.getByRole("button", { name: /Clear filters/ }).click();
  await expect.poll(() => requests.at(-1)?.get("channel")).toBeNull();
});

test("the inbox customer dropdown filters requests, resets pagination, survives reload, and clears", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/customers?*", route => route.fulfill({ json: pageOf([customer]) }));
  await page.route(`**/v1/customers/${customer.customer_id}`, route => route.fulfill({ json: { customer } }));
  await page.route("**/v1/feedback/grid?*", route => {
    requests.push(new URL(route.request().url()).searchParams);
    return route.fulfill({ json: gridOf([]) });
  });
  await page.goto("/inbox?page=3&channel=manual");
  await page.getByRole("button", { name: /^Filters/ }).click();
  const picker = page.getByRole("combobox", { name: "Customer search", exact: true });
  await picker.fill("Maryam");
  await page.getByRole("option", { name: /Maryam Ahmadi/ }).click();
  await expect.poll(() => requests.at(-1)?.get("customerId")).toBe(customer.customer_id);
  expect(requests.at(-1)?.get("page")).toBe("1");
  expect(requests.at(-1)?.get("channel")).toBe("manual");
  await page.reload();
  if (new URL(page.url()).pathname === "/experience") { await page.locator(".filter-toggle").waitFor(); await page.locator(".filter-toggle").click(); }
  await expect(picker).toHaveValue(customer.display_name);
  await page.getByRole("button", { name: /Clear filters/ }).click();
  await expect(picker).toHaveValue("");
  await expect.poll(() => requests.at(-1)?.get("customerId")).toBeNull();
});

test("inbox date filters include the full UTC end day, survive reload, and clear", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/feedback/grid?*", route => {
    requests.push(new URL(route.request().url()).searchParams);
    return route.fulfill({ json: gridOf([]) });
  });
  await page.goto("/inbox?page=3");
  await page.getByRole("button", { name: /^Filters/ }).click();
  await page.getByLabel("Start date", { exact: true }).fill("2026/10/01");
  await page.getByLabel("End date", { exact: true }).fill("2026/10/11");
  await page.getByLabel("End date", { exact: true }).press("Tab");
  await expect.poll(() => requests.at(-1)?.get("to")).toBe("2026-10-11T23:59:59.999999Z");
  expect(requests.at(-1)?.get("from")).toBe("2026-10-01T00:00:00.000Z");
  expect(requests.at(-1)?.get("page")).toBe("1");
  await page.reload();
  if (new URL(page.url()).pathname === "/experience") { await page.locator(".filter-toggle").waitFor(); await page.locator(".filter-toggle").click(); }
  await expect(page.getByLabel("Start date", { exact: true })).toHaveValue("2026/10/01");
  await expect(page.getByLabel("End date", { exact: true })).toHaveValue("2026/10/11");
  await page.getByRole("button", { name: /Clear filters/ }).click();
  await expect.poll(() => requests.at(-1)?.get("to")).toBeNull();
  expect(requests.at(-1)?.get("from")).toBeNull();
  await page.goto("/inbox?start=2026-10-12&end=2026-10-01");
  await page.getByRole("button", { name: /^Filters/ }).click();
  await expect(page.getByRole("alert")).toContainText("time period");
});

test("inbox reset clears filters, pagination and unfinished dates and disables when empty", async ({ page }) => {
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/feedback/grid?*", route => {
    requests.push(new URL(route.request().url()).searchParams);
    return route.fulfill({ json: gridOf([]) });
  });
  await page.goto("/inbox?status=new&page=3&channel=manual&severity=high&start=2026-10-01&q=test");
  await page.getByRole("button", { name: /^Filters/ }).click();
  const clear = page.getByRole("button", { name: /Clear filters/ });
  await expect(clear).toBeVisible();
  await page.getByLabel("End date", { exact: true }).fill("2026/");
  await clear.click();
  await expect.poll(() => requests.at(-1)?.get("page")).toBe("1");
  for (const key of ["q", "status", "channel", "severity", "from", "to"]) expect(requests.at(-1)?.get(key)).toBeNull();
  await expect(page.getByRole("combobox", { name: "Feedback status", exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Start date", { exact: true })).toHaveValue("");
  await expect(page.getByLabel("End date", { exact: true })).toHaveValue("");
  await expect(page.locator(".grid-search input")).toHaveValue("");
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(clear).toBeVisible();
  await expect(clear).toBeDisabled();
});

for (const width of [1892, 1024, 390]) {
  test(`inbox filter groups stay aligned without horizontal overflow at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([]) }));
    await page.goto("/inbox?end=2026-10-13");
  await page.getByRole("button", { name: /^Filters/ }).click();
    const panel = page.locator(".inbox-filters");
    await expect(panel).toBeVisible();
    await expect(page.getByRole("tablist", { name: "Feedback status" })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const controls = await panel.locator("select, .calendar-date-field input, .grid-search, .picker-input").evaluateAll(elements => elements.filter(element => element.getClientRects().length).map(element => element.getBoundingClientRect().height));
    expect(controls.every(height => height >= 36)).toBe(true);
    if (width > 1250) {

      const tops = await panel.locator(".inbox-filter-attributes > .grid-filter").evaluateAll(elements => elements.map(element => element.getBoundingClientRect().top));
      expect(Math.max(...tops) - Math.min(...tops)).toBeLessThan(1);
    }
    await page.screenshot({ path: testInfo.outputPath(`inbox-filters-${width}.png`), fullPage: true });
  });
}

test("admin records only a text complaint with customer and active problem category and safely retries", async ({ page }) => {
  const submissions: Record<string, unknown>[] = [];
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([]) }));
  await page.route("**/v1/customers?*", route => route.fulfill({ json: pageOf([customer]) }));
  await page.route("**/v1/admin/topics", route => route.fulfill({ json: {
    revision: 7, can_manage: true, topics: [
      { id: "root", code: "payment", title: "Payment", parent_id: null, is_active: true, sort_order: 0, keywords: [] },
      { id: "child", code: "payment.error", title: "Payment error", parent_id: "root", is_active: true, sort_order: 0, keywords: [] },
      { id: "deep", code: "payment.error.bank", title: "Bank", parent_id: "child", is_active: true, sort_order: 0, keywords: [] },
      { id: "other", code: "service", title: "Services", parent_id: null, is_active: true, sort_order: 1, keywords: [] },
      { id: "other-child", code: "service.delay", title: "Delay", parent_id: "other", is_active: true, sort_order: 0, keywords: [] },
      { id: "empty", code: "empty", title: "No subcategory", parent_id: null, is_active: true, sort_order: 2, keywords: [] },
      { id: "inactive-child", code: "payment.old", title: "Old", parent_id: "root", is_active: false, sort_order: 0, keywords: [] },
      { id: "retired", code: "old", title: "Disabled", parent_id: null, is_active: false, sort_order: 0, keywords: [] },
      { id: "hidden-child", code: "hidden", title: "Inactive child", parent_id: "retired", is_active: true, sort_order: 0, keywords: [] },
    ],
  } }));
  await page.route("**/v1/complaints", route => {
    submissions.push(route.request().postDataJSON());
    return submissions.length === 1
      ? route.fulfill({ status: 503, json: { error: "The registration was not completed; Try again." } })
      : route.fulfill({ status: 202, json: { event_id: "complaint-test", status: "accepted" } });
  });
  await page.goto("/complaints");
  await page.getByRole("button", { name: "Record customer complaint" }).click();
  const dialog = page.getByRole("dialog", { name: "Record customer complaint" });
  await dialog.getByLabel("Customer search").fill("Maryam");
  await dialog.getByRole("option", { name: /Maryam Ahmadi/ }).click();
  const parent = dialog.getByRole("combobox", { name: "Main category", exact: true });
  const child = dialog.getByRole("combobox", { name: "Subcategory", exact: true });
  const submit = dialog.getByRole("button", { name: "Submit complaint", exact: true });
  await expect(parent.locator("option")).toHaveText(["Selecting the main category", "Payment", "Services", "No subcategory"]);
  await expect(child).toBeDisabled();
  await expect(submit).toBeDisabled();
  await parent.selectOption("payment");
  await expect(child.locator("option")).toHaveText(["Subcategory selection", "Payment error", "Payment error › Bank"]);
  await expect(submit).toBeDisabled();
  await child.selectOption("payment.error.bank");
  await expect(submit).toBeEnabled();
  await parent.selectOption("service");
  await expect(child).toHaveValue("");
  await expect(child.locator("option")).toHaveText(["Subcategory selection", "Delay"]);
  await expect(submit).toBeDisabled();
  await parent.selectOption("empty");
  await expect(child).toBeDisabled();
  await expect(submit).toBeDisabled();
  await expect(dialog.getByRole("status")).toContainText("does not have an active subcategory");
  expect(submissions).toHaveLength(0);
  await parent.selectOption("payment");
  await child.selectOption("payment.error");
  await dialog.getByLabel("The text of the complaint").fill("Payment has been made but the order has not been registered.");
  await expect(dialog.locator('input[type="file"], audio')).toHaveCount(0);
  await expect(dialog.getByLabel(/Rating|Satisfaction|Recommendation/)).toHaveCount(0);
  await dialog.getByRole("button", { name: "Submit complaint", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("again");
  await dialog.getByRole("button", { name: "Submit complaint", exact: true }).click();
  await expect(dialog).not.toBeVisible();
  expect(submissions[1]).toEqual(submissions[0]);
  expect(submissions[0]).toMatchObject({ customer_id: customer.customer_id, category: "payment.error", revision: 7 });
  expect(Object.keys(submissions[0]).sort()).toEqual(["category", "comment", "customer_id", "revision", "submission_id"]);
});

test("non-admin users cannot open the complaint form", async ({ page }) => {
  await page.route("**/v1/auth/me", route => route.fulfill({ json: { ...sessionUser, permissions: ["cx.customer.read"] } }));
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([]) }));
  await page.goto("/inbox");
  await page.getByRole("button", { name: /^Filters/ }).click();
  await expect(page.getByRole("heading", { name: "Feedback inbox" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Record customer complaint" })).toHaveCount(0);
});

test("a changed taxonomy is refreshed before an admin retries a complaint", async ({ page }) => {
  let loads = 0;
  let attempts = 0;
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([]) }));
  await page.route("**/v1/customers?*", route => route.fulfill({ json: pageOf([customer]) }));
  await page.route("**/v1/admin/topics", route => route.fulfill({ json: {
    revision: ++loads, can_manage: true,
    topics: [
      { id: "root", code: "payment", title: "Payment", parent_id: null, is_active: true, sort_order: 0, keywords: [] },
      { id: "child", code: "payment.error", title: "Payment error", parent_id: "root", is_active: true, sort_order: 0, keywords: [] },
    ],
  } }));
  await page.route("**/v1/complaints", route => {
    if (++attempts === 1) return route.fulfill({ status: 409, json: { error: "Categories have changed." } });
    expect(route.request().postDataJSON().revision).toBe(2);
    return route.fulfill({ status: 202, json: { event_id: "complaint-test", status: "accepted" } });
  });
  await page.goto("/complaints");
  await page.getByRole("button", { name: "Record customer complaint" }).click();
  const dialog = page.getByRole("dialog", { name: "Record customer complaint" });
  await dialog.getByLabel("Customer search").fill("Maryam");
  await dialog.getByRole("option", { name: /Maryam Ahmadi/ }).click();
  await dialog.getByRole("combobox", { name: "Main category", exact: true }).selectOption("payment");
  await dialog.getByRole("combobox", { name: "Subcategory", exact: true }).selectOption("payment.error");
  await dialog.getByLabel("The text of the complaint").fill("Complaint about payment");
  await dialog.getByRole("button", { name: "Submit complaint", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("changed");
  await expect.poll(() => loads).toBe(2);
  await dialog.getByRole("button", { name: "Submit complaint", exact: true }).click();
  await expect(dialog).not.toBeVisible();
});

test("a direct feedback link opens the requested item outside the first page", async ({
  page,
}) => {
  await page.route("**/v1/feedback/grid?*", (route) =>
    route.fulfill({ json: gridOf([]) }),
  );
  await page.route(`**/v1/feedback/${feedback.id}`, (route) =>
    route.fulfill({ json: { feedback, analysis: null } }),
  );
  await page.goto(`/inbox?id=${feedback.id}`);

  await expect(
    page.getByRole("dialog", { name: "Feedback details" }),
  ).toContainText(feedback.comment);
});

test("intelligent analysis badges identify their values in the grid and detail", async ({ page }) => {
  await page.route("**/v1/admin/topics", route => route.fulfill({ json: {
    revision: 1, can_manage: true, topics: [
      { id: "root", code: "payment", title: "Payment", parent_id: null, is_active: true, sort_order: 0, keywords: [] },
      { id: "child", code: "payment.error", title: "Payment error", parent_id: "root", is_active: true, sort_order: 0, keywords: [] },
    ],
  } }));
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([
    { ...gridRow, severity: "critical", subcategory: "payment.error" },
  ]) }));
  await page.route(`**/v1/feedback/${feedback.id}`, route => route.fulfill({ json: {
    feedback, analysis: { ...analysis, result: { ...analysis.result, severity: "critical", subcategory: "payment.error" } },
  } }));
  await page.goto("/inbox");
  await page.getByRole("button", { name: /^Filters/ }).click();
  await expect(page.locator(".analysis-cell-labels .badge")).toHaveText([
    "Sentiment: Negative", "Classification: Payment › Payment error",
  ]);
  await page.locator(".data-grid tbody tr").first().click();
  await expect(page.locator(".analysis-labels .badge")).toHaveText([
    "Sentiment: Negative", "Classification: Payment › Payment error",
  ]);
});

test("NPS dashboard shows the response breakdown and distinguishes no data from zero", async ({ page }) => {
  await page.route("**/v1/overview?*", (route) => route.fulfill({ json: overview }));
  await page.goto("/");
  const report = page.getByRole("region", { name: "Report NPS" });
  await expect(report.locator(".nps-score")).toHaveText("+40");
  await expect(report).toContainText("10 responses");
  await expect(report.locator(".nps-breakdown")).toContainText("6 · 60%");
  await page.route("**/v1/overview?days=30", (route) => route.fulfill({ json: {
    ...overview, nps: { total: 0, promoters: 0, passives: 0, detractors: 0,
      promoter_percent: null, passive_percent: null, detractor_percent: null, score: null },
  } }));
  await page.getByLabel("Time frame").selectOption("30");
  await expect(report.locator(".nps-score")).toHaveText("—");
  await expect(report.getByRole("heading")).toHaveText("NPS score");
  await expect(report.locator(".nps-breakdown")).toHaveCount(0);
});

for (const [score, group] of [[0, "Detractor"], [6, "Detractor"], [7, "Passive"], [8, "Passive"], [9, "Promoter"], [10, "Promoter"]] as const) {
  test(`NPS ${score} is displayed as ${group} in the inbox`, async ({ page }) => {
    await page.route("**/v1/feedback/grid?*", (route) => route.fulfill({
      json: gridOf([{ ...gridRow, feedback_type: "nps", nps_score: score }]),
    }));
    await page.goto("/inbox");
  await page.getByRole("button", { name: /^Filters/ }).click();
    await expect(page.getByRole("cell", { name: new RegExp(`NPS:.*${group}`) })).toBeVisible();
  });
}

test("customer-page search works with the global search removed", async ({
  page,
}) => {
  await page.route("**/v1/customers?*", (route) =>
    route.fulfill({ json: pageOf([]) }),
  );
  await page.goto("/customers?q=old");
  await expect(page.getByLabel("Global search")).toHaveCount(0);
  await page.getByLabel("Searching for customers").fill("new-customer");
  await page.getByLabel("Searching for customers").press("Enter");
  await expect(page.getByLabel("Searching for customers")).toHaveValue("new-customer");
  await expect(page).toHaveURL(/q=new-customer/);
});

test("removed AI review routes do not expose obsolete approval controls", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: gridOf([]) }));
  await page.goto("/ai-review");
  await expect(page.getByRole("heading", { name: "Page not found", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Verification", exact: true })).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("an invalid successful API response is shown as an error", async ({
  page,
}) => {
  await page.route("**/v1/overview?*", (route) =>
    route.fulfill({
      contentType: "text/html",
      body: "<html>proxy error</html>",
    }),
  );
  await page.goto("/");
  await expect(page.getByRole("alert")).toContainText("The service response is not valid");
  await expect(page.getByRole("button", { name: "Retry" })).toBeVisible();
});

test("changing reporting filters ignores an older response arriving late", async ({
  page,
}) => {
  let releaseOld: () => void = () => {};
  const delayed = new Promise<void>((resolve) => {
    releaseOld = resolve;
  });
  await page.route("**/v1/overview?*", async (route) => {
    const days = new URL(route.request().url()).searchParams.get("days");
    if (days === "30") {
      await delayed;
      await route.fulfill({ json: { ...overview, total_interactions: 9999 } });
    } else
      await route.fulfill({ json: { ...overview, total_interactions: 70 } });
  });
  await page.goto("/");
  await expect(page.locator(".metric").first().locator("strong")).toHaveText(
    "70",
  );
  const oldRequest = page.waitForRequest("**/v1/overview?days=30");
  await page.getByLabel("Time frame").selectOption("30");
  await oldRequest;
  await page.getByLabel("Time frame").selectOption("7");
  await expect(page.locator(".metric").first().locator("strong")).toHaveText(
    "70",
  );
  const oldResponse = page.waitForResponse("**/v1/overview?days=30");
  releaseOld();
  await oldResponse;
  await expect(page.locator(".metric").first().locator("strong")).toHaveText(
    "70",
  );
});

test("the topic tree is edited as a tree, not as JSON", async ({ page }) => {
  const node = (id: string, title: string, parent_id: string | null, extra = {}) => ({
    id,
    parent_id,
    code: id,
    title,
    description: null,
    keywords: [],
    sort_order: 0,
    is_active: true,
    usage_count: 0,
    updated_by: "admin",
    updated_at: "2026-09-21T10:00:00Z",
    ...extra,
  });
  const topics = [
    node("payment", "Payment", null, { usage_count: 12, description: "Payment and gateway problems" }),
    node("payment.gateway", "Port error", "payment", { keywords: ["The port gave an error"] }),
    node("billing", "Finance and billing", null),
  ];
  let created: Record<string, unknown> | undefined;
  await page.route("**/v1/admin/topics", (route) => {
    if (route.request().method() === "POST") {
      created = route.request().postDataJSON();
      topics.push(node("t-new", String(created!.title), String(created!.parent_id)));
      return route.fulfill({ status: 201, json: topics[topics.length - 1] });
    }
    return route.fulfill({
      json: { revision: 3, decision_model: "typesafe/jev-1.13", can_manage: true, topics },
    });
  });

  await page.goto("/taxonomy");
  await expect(page.locator("textarea.mono")).toHaveCount(0);
  const grid = page.locator(".topic-grid");
  await expect(grid).toContainText("Port error");

  await page.getByRole("button", { name: "Hide subtopics" }).click();
  await expect(grid).not.toContainText("Port error");
  await page.getByRole("button", { name: "Show subtopics" }).click();

  await page.getByRole("button", { name: "Add subtopic to payment" }).click();
  const dialog = page.getByRole("dialog", { name: "New subtopic" });
  await dialog.getByLabel("Subject title").fill("Deducting money without registering an order");
  await dialog.getByLabel("Explanation").fill("The money has been reduced but the order has not been registered");
  await dialog.getByLabel("Examples of customer sentences").fill("I ran out of money\n\nThe order was not registered");
  await dialog.getByRole("button", { name: "Add subject" }).click();
  await expect(dialog).not.toBeVisible();

  expect(created).toMatchObject({
    parent_id: "payment",
    title: "Deducting money without registering an order",
    description: "The money has been reduced but the order has not been registered",
    keywords: ["I ran out of money", "The order was not registered"],
    is_active: true,
  });
  await expect(grid).toContainText("Deducting money without registering an order");
});

test("a technical task draft is editable, copyable and can be saved to a follow-up case", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  let status = "new";
  let drafts = 0;
  await page.route("**/v1/feedback/grid?*", (route) =>
    route.fulfill({ json: gridOf([{ ...gridRow, feedback_type: "complaint", status }]) }),
  );
  await page.route(`**/v1/feedback/${feedback.id}`, (route) =>
    route.fulfill({ json: { feedback: { ...feedback, feedback_type: "complaint", status }, analysis } }),
  );
  await page.route("**/v1/feedback/*/review", (route) => {
    status = route.request().postDataJSON().status;
    return route.fulfill({ json: { ...feedback, status } });
  });
  await page.route("**/v1/feedback/*/follow-up-task", (route) => {
    drafts++;
    return route.fulfill({
      json: {
        title: "Payment gateway error during checkout",
        priority: "High",
        text: "Title: Payment gateway error during checkout\nPriority: High\n\nDescription of the problem:\nThe customer’s payment encounters an error.",
        generated_by: "ai",
        model: "google/gemini-3.1-flash-lite",
      },
    });
  });

  await page.goto(`/complaints?id=${feedback.id}`);

  const drawer = page.getByRole("dialog", { name: "Complaint details" });
  await drawer.getByRole("button", { name: "Task text for the technical team" }).click();

  const task = page.getByRole("dialog", { name: "Suggested task for the technical team" });
  const text = task.getByLabel("Task text");
  await expect(text).toHaveValue(/Payment gateway error during checkout/);
  await expect(task).toContainText("Priority: High");
  expect(status).toBe("new");
  await expect(task.getByRole("link", { name: "Save to case" })).toHaveAttribute("href", `/complaints/cases?feedback=${feedback.id}`);

  await text.fill("Title: Edited by operator");
  await task.getByRole("button", { name: "Text copy" }).click();
  await expect(task.getByRole("button", { name: "Copied" })).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe("Title: Edited by operator");
  expect(drafts).toBe(1);
});

test("the topic filter picks a parent first, then one of its subtopics", async ({ page }) => {
  const node = (id: string, title: string, parent_id: string | null) => ({
    id, parent_id, code: id, title, keywords: [], sort_order: 0, is_active: true, usage_count: 0,
    updated_at: "2026-09-21T10:00:00Z",
  });
  await page.route("**/v1/admin/topics", (route) =>
    route.fulfill({
      json: {
        revision: 1,
        can_manage: true,
        topics: [
          node("payment", "Payment", null),
          node("payment.gateway", "Port error", "payment"),
          node("t-deep", "Bank Mellat", "payment.gateway"),
          node("billing", "Finance", null),
        ],
      },
    }),
  );
  const requests: URLSearchParams[] = [];
  await page.route("**/v1/feedback/grid?*", (route) => {
    requests.push(new URL(route.request().url()).searchParams);
    return route.fulfill({ json: gridOf([{ ...gridRow, subcategory: "payment.gateway" }]) });
  });

  await page.goto("/inbox");
  await page.getByRole("button", { name: /^Filters/ }).click();
  await expect(page.locator(".data-grid")).toContainText("Payment › Port error");
  const topic = page.getByRole("combobox", { name: "Main category", exact: true });
  const sub = page.getByRole("combobox", { name: "Subcategory", exact: true });
  await expect(topic.locator("option")).toHaveText(["All categories", "Finance", "Payment"]);
  await expect(sub).toBeDisabled();

  await topic.selectOption("payment");
  await expect(sub.locator("option")).toHaveText(["All subcategories", "Port error", "Port error › Bank Mellat"]);
  await sub.selectOption("t-deep");
  await expect.poll(() => requests.at(-1)?.get("subcategory")).toBe("t-deep");
  expect(requests.at(-1)?.get("category")).toBe("payment");
  await page.reload();
  if (new URL(page.url()).pathname === "/experience") { await page.locator(".filter-toggle").waitFor(); await page.locator(".filter-toggle").click(); }
  await expect(topic).toHaveValue("payment");
  await expect(sub).toHaveValue("t-deep");

  await topic.selectOption("billing");
  await expect(sub).toBeDisabled();
  await expect(sub).toHaveValue("");
  await expect.poll(() => requests.at(-1)?.get("subcategory")).toBeNull();
  await topic.selectOption("");
  await expect(sub).toBeDisabled();
  await expect.poll(() => requests.at(-1)?.get("category")).toBeNull();
});

test("an operator signs in, is told about a wrong password, and can sign out", async ({ page }) => {
  let signedIn = false;
  await page.unroute("**/v1/auth/me");
  await page.route("**/v1/auth/me", (route) =>
    signedIn ? route.fulfill({ json: sessionUser }) : route.fulfill({ status: 401, json: {} }),
  );
  await page.route("**/v1/auth/login", (route) => {
    const body = route.request().postDataJSON();
    if (body.username !== "admin" || body.password !== "admin123")
      return route.fulfill({ status: 401, json: { error: "Username or password is incorrect." } });
    signedIn = true;
    return route.fulfill({ json: sessionUser });
  });
  await page.route("**/v1/auth/logout", (route) => {
    signedIn = false;
    return route.fulfill({ status: 204 });
  });
  await page.route("**/v1/overview?*", (route) => route.fulfill({ json: overview }));

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("Username").fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("wrong");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("incorrect");

  await page.getByLabel("Password", { exact: true }).fill("admin123");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.locator(".current-user-menu")).toContainText("Administrator");

  await page.locator(".current-user-menu > summary").click();
  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
});

test("an expired session on any call returns to the sign-in screen", async ({ page }) => {
  await page.route("**/v1/overview?*", (route) => route.fulfill({ status: 401, json: {} }));
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
});

test("the theme toggle cycles system, light and dark and is remembered", async ({ page }) => {
  await page.route("**/v1/overview?*", (route) => route.fulfill({ json: overview }));
  await page.goto("/");
  const html = page.locator("html");
  const toggle = page.getByRole("button", { name: /^(System theme|Light mode|Dark mode)$/ });
  await expect(page.locator(".topbar")).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^(System theme|Light mode|Dark mode)$/ })).toHaveCount(1);

  await expect(html).not.toHaveAttribute("data-theme");
  await toggle.click();
  await expect(html).toHaveAttribute("data-theme", "light");
  await toggle.click();
  await expect(html).toHaveAttribute("data-theme", "dark");

  await page.reload();
  if (new URL(page.url()).pathname === "/experience") { await page.locator(".filter-toggle").waitFor(); await page.locator(".filter-toggle").click(); }
  await expect(html).toHaveAttribute("data-theme", "dark");
  await toggle.click();
  await expect(html).not.toHaveAttribute("data-theme");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Open the menu" }).click();
  await page.keyboard.press("Escape");
  await expect(toggle).toBeVisible();
  await toggle.click();
  await expect(html).toHaveAttribute("data-theme", "light");
});
