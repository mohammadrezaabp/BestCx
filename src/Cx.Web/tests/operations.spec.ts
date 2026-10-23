import { test, expect } from "@playwright/test";

const caseId = "10000000-0000-4000-8000-000000000001";
const staffId = "20000000-0000-4000-8000-000000000001";
const feedbackId = "30000000-0000-4000-8000-000000000001";
const customer = { customer_id: "C-OPS", display_name: "Maryam Ahmadi", attributes: {}, first_seen_at: "2026-10-01T00:00:00Z", last_seen_at: "2026-10-13T00:00:00Z" };
const directory = { teams: [], staff: [{ id: staffId, username: "admin", display_name: "Manager", role: "admin", enabled: true }], current_staff_id: staffId };
const policy = { id: "40000000-0000-4000-8000-000000000001", name: "Medium deadline", priority: "medium", response_minutes: 240, resolution_minutes: 1440, enabled: true,
  calendar: { time_zone: "UTC", working_days: [6, 0, 1, 2, 3], starts_at: "09:00", ends_at: "17:00", holidays: [] } };
const workCase = { id: caseId, reference: "CX-TEST", customer_id: customer.customer_id, customer_name: customer.display_name, title: "Payment tracking", description: "The payment encountered an error",
  priority: "medium", status: "new", team_id: null, assignee_id: null, problem_id: null, topic: null, version: 1, reopen_count: 0,
  response_due_at: "2026-10-14T06:30:00Z", resolution_due_at: "2026-10-18T06:30:00Z", first_response_at: null, pause_started_at: null,
  resolution_text: "", response_overdue: false, resolution_overdue: false, created_at: "2026-10-13T06:30:00Z", updated_at: "2026-10-13T06:30:00Z" };
const metric = { name: "All", total: 1, resolved: 0, overdue: 0, reopened: 0, response_measured: 0, resolution_measured: 0, response_within_sla: 0,
  resolution_within_sla: 0, average_response_minutes: null, average_resolution_minutes: null, aftercare_csat: null, aftercare_responses: 0 };
test.beforeEach(async ({ page }) => {
  await page.route("**/v1/**", route => route.fulfill({ status: 404, json: {} }));
  await page.route("**/v1/auth/me", route => route.fulfill({ json: { username: "admin", display_name: "Manager", tenant_id: "bestcx", mode: "login", permissions: ["cx.admin"] } }));
  await page.route("**/v1/admin/topics*", route => route.fulfill({ json: { revision: 1, topics: [] } }));
  await page.route("**/v1/customers?*", route => route.fulfill({ json: { items: [customer], has_more: false } }));
  await page.route(`**/v1/customers/${customer.customer_id}`, route => route.fulfill({ json: { customer } }));
  await page.route("**/v1/operations/directory", route => route.fulfill({ json: directory }));
  await page.route("**/v1/operations/policies", route => route.fulfill({ json: [policy] }));
  for (const path of ["rules", "workflows", "executions", "problems", "campaigns", "journeys", "notifications", "follow-up-customers*"])
    await page.route(`**/v1/operations/${path}`, route => route.fulfill({ json: [] }));
  await page.route("**/v1/operations/notifications/grid?*", route => route.fulfill({ json: { items: [], total: 0, unread_count: 0, page: 1, page_size: 20 } }));
  await page.route("**/v1/operations/cases?*", route => route.fulfill({ json: { items: [workCase], total: 1, page: 1, page_size: 20 } }));
  await page.route("**/v1/operations/cases", route => route.fulfill({ json: { items: [workCase], total: 1, page: 1, page_size: 20 } }));
  await page.route(`**/v1/operations/cases/${caseId}`, route => route.fulfill({ json: { case: workCase, activities: [], attachments: [], feedback_ids: [] } }));
  await page.route("**/v1/operations/outcomes?*", route => route.fulfill({ json: { summary: metric, teams: [], topics: [] } }));
});

test("feedback preserves access to a linked legacy case without creating another", async ({ page }) => {
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } }));
  await page.route(`**/v1/feedback/${feedbackId}`, route => route.fulfill({ json: { feedback: { id: feedbackId, customer_id: customer.customer_id, status: "new", comment: "Payment issue", feedback_type: "generic", submitted_at: workCase.created_at } } }));
  await page.route(`**/v1/operations/cases/by-feedback/${feedbackId}`, route => route.fulfill({ json: { id: caseId } }));
  await page.goto(`/experience?id=${feedbackId}`);
  await expect(page.getByRole("link", { name: "Create case", exact: true })).toHaveCount(0);
  await page.getByRole("link", { name: "Case", exact: true }).click();
  await expect(page).toHaveURL(`/complaints/cases/${caseId}`);
  await expect(page.getByRole("heading", { name: workCase.title, exact: true })).toBeVisible();
});

test("a complaint opens its automatically generated case without asking for case data or creating a duplicate", async ({ page }) => {
  await page.clock.install();
  let ready = false;
  let creates = 0;
  await page.route(`**/v1/feedback/${feedbackId}`, route => route.fulfill({ json: { feedback: {
    id: feedbackId, customer_id: customer.customer_id, status: "new", reviewed: false,
    comment: workCase.description, feedback_type: "complaint", submitted_at: workCase.created_at,
  } } }));
  await page.route(`**/v1/operations/cases/by-feedback/${feedbackId}`, route => route.fulfill({ json: { id: ready ? caseId : null } }));
  await page.route("**/v1/operations/cases", route => {
    if (route.request().method() === "POST") creates++;
    return route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } });
  });
  await page.goto(`/cases?feedback=${feedbackId}`);
  await expect(page.getByRole("dialog", { name: "Complaint case", exact: true })).toContainText("Filing of complaint information");
  await expect(page.getByLabel("File title")).toHaveCount(0);
  await expect(page.getByLabel("Description of the case")).toHaveCount(0);
  ready = true;
  await page.clock.runFor(3100);
  await expect(page).toHaveURL(`/complaints/cases/${caseId}`);
  await expect(page.locator(".case-main")).toContainText(workCase.description);
  expect(creates).toBe(0);
});

test("automatically created complaint cases appear in the queue without a manual refresh", async ({ page }) => {
  await page.clock.install();
  let ready = false;
  await page.route("**/v1/operations/cases?*", route => route.fulfill({ json: { items: ready ? [workCase] : [], total: ready ? 1 : 0, page: 1, page_size: 20 } }));
  await page.goto("/cases");
  await expect(page.getByText("No file was found with these filters")).toBeVisible();
  await expect(page.getByRole("button", { name: "New file", exact: true })).toHaveCount(0);
  ready = true;
  await page.clock.runFor(10100);
  await expect(page.getByRole("link", { name: workCase.title, exact: true })).toBeVisible();
  await page.getByRole("link", { name: "Record customer complaint", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Record customer complaint", exact: true })).toBeVisible();
});

test("a technical draft for an existing complaint case stays an internal note", async ({ page }) => {
  const text = "Check payment error by technical team";
  await page.route("**/v1/feedback/grid?*", route => route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } }));
  await page.route(`**/v1/feedback/${feedbackId}`, route => route.fulfill({ json: { feedback: {
    id: feedbackId, customer_id: customer.customer_id, status: "new", reviewed: false,
    comment: workCase.description, feedback_type: "complaint", submitted_at: workCase.created_at,
  } } }));
  await page.route(`**/v1/operations/cases/by-feedback/${feedbackId}`, route => route.fulfill({ json: { id: caseId } }));
  await page.route(`**/v1/feedback/${feedbackId}/follow-up-task`, route => route.fulfill({ json: { text, priority: "High" } }));
  await page.goto(`/inbox?id=${feedbackId}`);
  await page.getByRole("button", { name: "Task text for the technical team", exact: true }).click();
  await page.getByRole("link", { name: "Save to case", exact: true }).click();
  await expect(page).toHaveURL(`/complaints/cases/${caseId}`);
  await expect(page.getByRole("textbox", { name: "Internal note text", exact: true })).toHaveValue(text);
  await expect(page.getByRole("button", { name: "Internal note", exact: true })).toHaveAttribute("aria-pressed", "true");
});

test("public replies and one-click handling use the current case version without manual management fields", async ({ page }, testInfo) => {
  let current = { ...workCase, stage_index: -1, workflow: { name: "Direct processing", stages: [{ title: "Technical review", team_id: null, description: "" }] } };
  const messages: Record<string, unknown>[] = [];
  const updates: Record<string, unknown>[] = [];
  await page.route(`**/v1/operations/cases/${caseId}`, route => {
    if (route.request().method() === "PUT") {
      const input = route.request().postDataJSON(); updates.push(input); current = { ...current, ...input, version: current.version + 1 };
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: { case: current, activities: [], attachments: [], feedback_ids: [] } });
  });
  await page.route(`**/v1/operations/cases/${caseId}/messages`, route => { messages.push(route.request().postDataJSON()); current.version++; return route.fulfill({ status: 204 }); });
  await page.route(`**/v1/operations/cases/${caseId}/workflow`, route => {
    updates.push(route.request().postDataJSON()); current = { ...current, status: "in_progress", stage_index: 0, version: current.version + 1 };
    return route.fulfill({ json: { can_access: true } });
  });
  await page.goto(`/complaints/cases/${caseId}`);
  await page.getByLabel("Response text to the customer").fill("The payment was checked");
  await page.getByRole("button", { name: "Send response", exact: true }).click();
  await expect(page.getByLabel("Response text to the customer")).toHaveValue("");
  expect(messages[0].public).toBe(true);
  const management = page.getByRole("region", { name: "Case workflow", exact: true });
  await expect(management.getByRole("button")).toHaveCount(1);
  await expect(page.getByLabel("Status of the file", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("Resolution for the customer")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("case-handling-desktop.png"), fullPage: true });
  await management.getByRole("button", { name: "Start case handling", exact: true }).click();
  await expect.poll(() => updates.length).toBe(1);
  expect(updates[0]).toEqual({ version: 2, action: "start", text: "" });
  await expect(page.getByRole("button", { name: "Start case handling", exact: true })).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("case-handling-mobile.png"), fullPage: true });
});

test("starting handling delegates routing to the server without sending manual assignment or priority", async ({ page }) => {
  let current = { ...workCase, priority: "high", team_id: "team-1", assignee_id: "other-staff", problem_id: "problem-1", resolution_text: "Previously registered information",
    stage_index: -1, workflow: { name: "Direct processing", stages: [{ title: "Technical review", team_id: "team-1", description: "" }] } };
  const updates: Record<string, unknown>[] = [];
  await page.route(`**/v1/operations/cases/${caseId}`, route => {
    if (route.request().method() === "PUT") {
      const input = route.request().postDataJSON(); updates.push(input); current = { ...current, ...input, version: current.version + 1 };
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: { case: current, activities: [], attachments: [], feedback_ids: [] } });
  });
  await page.route(`**/v1/operations/cases/${caseId}/workflow`, route => {
    updates.push(route.request().postDataJSON()); current = { ...current, status: "in_progress", stage_index: 0, version: current.version + 1 };
    return route.fulfill({ json: { can_access: true } });
  });
  await page.goto(`/complaints/cases/${caseId}`);
  await page.getByRole("button", { name: "Start case handling", exact: true }).click();
  await expect.poll(() => updates.length).toBe(1);
  expect(updates[0]).toEqual({ version: 1, action: "start", text: "" });
  await expect(page.getByRole("button", { name: "Start case handling", exact: true })).toHaveCount(0);
  await expect(page.locator(".case-main")).toContainText("Previously registered information");
});

test("failed handling can be retried without reporting success or changing the case version", async ({ page }) => {
  let attempts = 0;
  let current = { ...workCase, stage_index: -1, workflow: { name: "Direct processing", stages: [{ title: "Technical review", team_id: null, description: "" }] } };
  await page.route(`**/v1/operations/cases/${caseId}`, route => {
    if (route.request().method() === "PUT") {
      attempts++;
      expect(route.request().postDataJSON().version).toBe(1);
      if (attempts === 1) return route.fulfill({ status: 503, json: { error: "Temporary error" } });
      current = { ...current, status: "in_progress", version: 2 };
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: { case: current, activities: [], attachments: [], feedback_ids: [] } });
  });
  await page.route(`**/v1/operations/cases/${caseId}/workflow`, route => {
    attempts++;
    expect(route.request().postDataJSON()).toEqual({ version: 1, action: "start", text: "" });
    if (attempts === 1) return route.fulfill({ status: 503, json: { error: "Temporary error" } });
    current = { ...current, status: "in_progress", stage_index: 0, version: 2 };
    return route.fulfill({ json: { can_access: true } });
  });
  await page.goto(`/complaints/cases/${caseId}`);
  const start = page.getByRole("button", { name: "Start case handling", exact: true });
  await start.click();
  await expect(page.getByRole("alert")).toContainText("Temporary error");
  await expect(start).toBeEnabled();
  await start.click();
  await expect(start).toHaveCount(0);
  expect(attempts).toBe(2);
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test("English holiday calendar works inside the policy dialog and Escape leaves the editor open", async ({ page }) => {
  const saved: Record<string, unknown>[] = [];
  await page.route("**/v1/operations/policies", route => {
    if (route.request().method() === "PUT") { saved.push(route.request().postDataJSON()); return route.fulfill({ status: 204 }); }
    return route.fulfill({ json: [policy] });
  });
  await page.goto("/operations-settings");
  await page.getByRole("button", { name: "Edit", exact: true }).click();
  const editor = page.getByRole("dialog", { name: "Edit the processing deadline", exact: true });
  await editor.getByRole("button", { name: "Open the calendar Holiday", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Calendar Holiday", exact: true })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(editor).toBeVisible();
  await editor.getByRole("button", { name: "Open the calendar Holiday", exact: true }).click();
  const calendar = page.getByRole("dialog", { name: "Calendar Holiday", exact: true });
  await calendar.getByLabel("Year Holiday", { exact: true }).selectOption("2026");
  await calendar.getByLabel("Month Holiday", { exact: true }).selectOption("10");
  await calendar.getByRole("button", { name: "13 October 2026", exact: true }).click();
  await editor.getByRole("button", { name: "Add closure", exact: true }).click();
  await editor.getByRole("button", { name: "Policy storage", exact: true }).click();
  await expect.poll(() => saved.length).toBe(1);
  expect((saved[0].calendar as { holidays: string[] }).holidays).toEqual(["2026-10-13"]);
});

for (const [path, title] of [["cases", "Cases"], ["operations-settings", "Case settings"], ["problems", "Common problems and corrective actions"], ["campaigns", "Survey campaigns"], ["journeys", "Customer journey map"], ["outcomes", "Service outcomes"], ["follow-up-customers", "Follow-up customers"], ["notifications", "Notifications"]]) {
  test(`operational page ${path} loads without mobile overflow`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(`/${path}`);
    await expect(page.getByRole("heading", { name: title, exact: true })).toBeVisible();
    await expect(page.getByRole("alert")).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(errors).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`${path}-mobile.png`), fullPage: true });
  });
}
