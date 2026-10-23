import { test, expect } from "@playwright/test";

const caseId = "10000000-0000-4000-8000-000000000001";
const staffId = "20000000-0000-4000-8000-000000000001";
const tech = "30000000-0000-4000-8000-000000000001";
const finance = "30000000-0000-4000-8000-000000000002";
const stages = [
  { title: "Technical review", team_id: tech, description: "Checking the transaction log" },
  { title: "Refund", team_id: finance, description: "Checking and correcting the financial transaction" },
];
const workflow = { id: "40000000-0000-4000-8000-000000000001", name: "Payment processing", description: "Fix payment error and refund", topic: null, stages, enabled: true, version: 1 };
const directory = { current_staff_id: staffId,
  staff: [{ id: staffId, username: "admin", display_name: "Technical expert", role: "admin", enabled: true }],
  teams: [
    { id: tech, name: "Technical team", description: "Fix software malfunction", members: [staffId], lead_id: staffId, enabled: true },
    { id: finance, name: "Financial team", description: "Check and refund", members: [], lead_id: null, enabled: true },
  ],
};
const baseCase = { id: caseId, reference: "CX-WORKFLOW", customer_id: "C-OPS", customer_name: "Maryam Ahmadi", title: "Payment error", description: "The money has been deducted and the order has not been registered",
  status: "new", priority: "high", team_id: null as string | null, team_name: null as string | null, assignee_id: null,
  assignee_name: null, problem_id: null, topic: null, version: 1, stage_index: -1, workflow: { name: workflow.name, stages },
  routing_reason: "The technical team was selected based on payment errors", routing_model: "test-ai", resolution_text: "", reopen_count: 0,
  response_due_at: "2026-10-19T09:00:00Z", resolution_due_at: "2026-10-20T09:00:00Z", first_response_at: null,
  pause_started_at: null, response_overdue: false, resolution_overdue: false, created_at: "2026-10-18T09:00:00Z", updated_at: "2026-10-18T09:00:00Z" };

test.beforeEach(async ({ page }) => {
  await page.route("**/v1/**", route => route.fulfill({ status: 404, json: {} }));
  await page.route("**/v1/auth/me", route => route.fulfill({ json: { username: "admin", display_name: "Manager", tenant_id: "bestcx", mode: "login", permissions: ["cx.admin"] } }));
  await page.route("**/v1/admin/topics*", route => route.fulfill({ json: { revision: 1, topics: [] } }));
  await page.route("**/v1/operations/directory", route => route.fulfill({ json: directory }));
  for (const resource of ["policies", "rules", "executions"]) await page.route(`**/v1/operations/${resource}`, route => route.fulfill({ json: [] }));
  await page.route("**/v1/operations/workflows", route => route.fulfill({ json: [workflow] }));
  await page.route("**/v1/operations/cases?*", route => route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } }));
  await page.route("**/v1/operations/cases", route => route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } }));
  await page.route("**/v1/customers?*", route => route.fulfill({ json: { items: [], has_more: false } }));
});

test("workflows can define, reorder and assign their stages to teams", async ({ page }, testInfo) => {
  const saved: any[] = [];
  await page.route("**/v1/operations/workflows", route => {
    if (route.request().method() === "PUT") { saved.push(route.request().postDataJSON()); return route.fulfill({ status: 204 }); }
    return route.fulfill({ json: [workflow] });
  });
  await page.goto("/operations-settings");
  await page.getByRole("button", { name: "Workflows", exact: true }).click();
  await page.getByRole("button", { name: "Add Workflow", exact: true }).click();
  const editor = page.getByRole("dialog", { name: "Workflow", exact: true });
  await editor.getByLabel("Name of the procedure", { exact: true }).fill("Payment procedure");
  await editor.getByLabel("Description of the procedure for intelligent selection").fill("Payment error handling");
  await editor.getByLabel("Step title", { exact: true }).fill("Technical review");
  await editor.getByLabel("The team responsible for the stage", { exact: true }).selectOption(tech);
  await editor.getByRole("button", { name: "Add step", exact: true }).click();
  await editor.getByLabel("Step title", { exact: true }).nth(1).fill("Refund");
  await editor.getByLabel("The team responsible for the stage", { exact: true }).nth(1).selectOption(finance);
  await editor.getByRole("button", { name: "Move up", exact: true }).nth(1).click();
  await expect(editor.getByLabel("Step title", { exact: true }).first()).toHaveValue("Refund");
  await editor.getByRole("button", { name: "Move down", exact: true }).first().click();
  await page.screenshot({ path: testInfo.outputPath("workflow-editor-desktop.png"), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("workflow-editor-mobile.png"), fullPage: true });
  await editor.getByRole("button", { name: "Save workflow", exact: true }).click();
  await expect(editor).toHaveCount(0);
  expect(saved).toHaveLength(1);
  expect(saved[0].stages.map((stage: any) => [stage.title, stage.team_id])).toEqual([["Technical review", tech], ["Refund", finance]]);
});

test("team responsibilities are editable for AI routing", async ({ page }) => {
  const saved: any[] = [];
  await page.route("**/v1/operations/teams", route => { saved.push(route.request().postDataJSON()); return route.fulfill({ status: 204 }); });
  await page.goto("/operations-settings");
  await page.getByRole("button", { name: "Teams", exact: true }).click();
  await page.getByRole("row").filter({ hasText: "Technical team" }).getByRole("button", { name: "Edit", exact: true }).click();
  const editor = page.getByRole("dialog", { name: "Handling team", exact: true });
  await editor.getByLabel("Description of team tasks for intelligent selection").fill("Troubleshoot the payment gateway, check the log and transaction error");
  await editor.getByRole("button", { name: "Save the team", exact: true }).click();
  await expect(editor).toHaveCount(0);
  expect(saved[0].description).toContain("payment gateway");
});

test("teams record notes, advance the dynamic workflow and close it with a real resolution", async ({ page }, testInfo) => {
  let current = { ...baseCase };
  const transitions: any[] = [], notes: any[] = [];
  await page.route(`**/v1/operations/cases/${caseId}`, route => route.fulfill({ json: { case: current, activities: [], attachments: [], feedback_ids: [] } }));
  await page.route(`**/v1/operations/cases/${caseId}/workflow`, route => {
    const input = route.request().postDataJSON(); transitions.push(input);
    expect(input.version).toBe(current.version);
    current = { ...current, version: current.version + 1, status: input.action === "close" ? "closed" : "in_progress",
      stage_index: input.action === "next" ? 1 : input.action === "start" ? 0 : current.stage_index,
      team_id: input.action === "next" ? finance : input.action === "start" ? tech : current.team_id,
      team_name: input.action === "next" ? "Financial team" : "Technical team", resolution_text: input.action === "close" ? input.text : "" };
    return route.fulfill({ json: { can_access: true } });
  });
  await page.route(`**/v1/operations/cases/${caseId}/messages`, route => { notes.push(route.request().postDataJSON()); current.version++; return route.fulfill({ status: 204 }); });
  await page.goto(`/complaints/cases/${caseId}`);
  await page.getByRole("button", { name: "Start case handling", exact: true }).click();
  const panel = page.getByRole("region", { name: "Case workflow", exact: true });
  await expect(panel.locator('[aria-current="step"]')).toContainText("Technical review");
  await page.locator(".case-transition > summary").waitFor();
  if (await page.locator(".case-transition:not([open]) > summary").count()) await page.locator(".case-transition:not([open]) > summary").click();
  await expect(panel.getByRole("button", { name: "Next stage", exact: true })).toBeDisabled();
  await panel.getByRole("textbox").fill("The disorder was resolved; Refund check is required");
  await page.screenshot({ path: testInfo.outputPath("case-workflow-desktop.png"), fullPage: true });
  await panel.getByRole("button", { name: "Next stage", exact: true }).click();
  await expect(panel.locator('[aria-current="step"]')).toContainText("Refund");
  await expect(panel.getByRole("button", { name: "Next stage", exact: true })).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("case-workflow-mobile.png"), fullPage: true });
  await page.locator(".case-transition > summary").waitFor();
  if (await page.locator(".case-transition:not([open]) > summary").count()) await page.locator(".case-transition:not([open]) > summary").click();
  await panel.getByRole("textbox").fill("The amount was returned to the customer’s account");
  await panel.getByRole("button", { name: "Resolve case", exact: true }).click();
  await expect(panel).toContainText("The proceedings are over");
  await expect(panel.getByRole("textbox")).toHaveCount(0);
  await expect(page.locator(".case-main")).toContainText("The amount was returned to the customer’s account");
  expect(transitions.map(input => input.action)).toEqual(["start", "next", "close"]);
});

test("a handoff sends the previous team back to its queue", async ({ page }) => {
  await page.route("**/v1/auth/me", route => route.fulfill({ json: { username: "admin", display_name: "Agent", tenant_id: "bestcx", mode: "login", permissions: ["cx.work.read", "cx.work.manage", "cx.customer.read"] } }));
  await page.route(`**/v1/operations/cases/${caseId}`, route => route.fulfill({ json: { case: { ...baseCase, status: "in_progress", stage_index: 0, team_id: tech }, activities: [], attachments: [], feedback_ids: [] } }));
  await page.route(`**/v1/operations/cases/${caseId}/workflow`, route => route.fulfill({ json: { can_access: false } }));
  await page.goto(`/complaints/cases/${caseId}`);
  await page.locator(".case-transition > summary").waitFor();
  if (await page.locator(".case-transition:not([open]) > summary").count()) await page.locator(".case-transition:not([open]) > summary").click();
  await page.getByRole("region", { name: "Case workflow", exact: true }).getByRole("textbox").fill("The technical review was completed");
  await page.getByRole("button", { name: "Next stage", exact: true }).click();
  await expect(page).toHaveURL("/complaints/cases");
  await expect(page.getByRole("status").filter({ hasText: "The case was referred to the team in charge of the stage" })).toBeVisible();
});

test("failed stage transitions preserve the team note for retry", async ({ page }) => {
  await page.route(`**/v1/operations/cases/${caseId}`, route => route.fulfill({ json: { case: { ...baseCase, status: "in_progress", stage_index: 0, team_id: tech }, activities: [], attachments: [], feedback_ids: [] } }));
  await page.route(`**/v1/operations/cases/${caseId}/workflow`, route => route.fulfill({ status: 409, json: { error: "The file has changed" } }));
  await page.goto(`/complaints/cases/${caseId}`);
  const panel = page.getByRole("region", { name: "Case workflow", exact: true });
  await page.locator(".case-transition > summary").waitFor();
  if (await page.locator(".case-transition:not([open]) > summary").count()) await page.locator(".case-transition:not([open]) > summary").click();
  await panel.getByRole("textbox").fill("Important note of the technical team");
  await panel.getByRole("button", { name: "Next stage", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("The file has changed");
  await expect(panel.getByRole("textbox")).toHaveValue("Important note of the technical team");
  await expect(panel.locator('[aria-current="step"]')).toContainText("Technical review");
});
