import { test, expect } from "@playwright/test";

const id = "10000000-0000-4000-8000-000000000001";
const invitation = { id: "20000000-0000-4000-8000-000000000001", campaign_name: "Consent after processing", question: "How satisfied are you with the handling?", metric: "csat", expires_at: "2030-10-13T00:00:00Z", answered_at: null, declined_at: null, score: null };
const item = { id, reference: "CX-TEST", title: "Payment processing", priority: "medium", status: "resolved", version: 3, resolution_text: "The problem is solved", created_at: "2026-10-13T00:00:00Z", updated_at: "2026-10-13T00:00:00Z", reopen_count: 0 };
test.beforeEach(async ({ page }) => {
  await page.route("**/v1/portal/**", route => route.fulfill({ status: 404, json: {} }));
  await page.route("**/v1/portal/auth/me", route => route.fulfill({ json: { customer_id: "C-OPS", username: "customer", display_name: "Maryam Ahmadi" } }));
  await page.route("**/v1/portal/cases?*", route => route.fulfill({ json: { items: [item], total: 1, page: 1, page_size: 20 } }));
  await page.route("**/v1/portal/history?*", route => route.fulfill({ json: { items: [], total: 0, page: 1, page_size: 20 } }));
  await page.route(`**/v1/portal/cases/${id}`, route => route.fulfill({ json: { case: item, activities: [{ id: "message", actor: "Agent", kind: "message", text: "The payment was checked", created_at: "2026-10-13T00:00:00Z" }], attachments: [] } }));
  await page.route("**/v1/portal/surveys", route => route.fulfill({ json: [invitation] }));
  await page.route(`**/v1/portal/surveys/${invitation.id}`, route => route.fulfill({ json: invitation }));
  await page.route("**/v1/portal/notifications", route => route.fulfill({ json: [] }));
});
test("customer can inspect the public timeline and request reopening with a reason", async ({ page }) => {
  const actions: Record<string, unknown>[] = [];
  await page.route(`**/v1/portal/cases/${id}/action`, route => { actions.push(route.request().postDataJSON()); return route.fulfill({ status: 204 }); });
  await page.goto("/tracking");
  await page.getByRole("link", { name: /Payment processing/ }).click();
  await expect(page.getByText("The payment was checked", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "The problem still remains", exact: true }).click();
  await page.getByLabel("Reason for reopening").fill("The error is repeated again");
  await page.getByRole("button", { name: "Open registration", exact: true }).click();
  await expect.poll(() => actions.length).toBe(1);
  expect(actions[0]).toEqual({ version: 3, action: "reopen", comment: "The error is repeated again" });
});
test("survey permalink collects a valid score and sends only its invitation answer", async ({ page }) => {
  const answers: Record<string, unknown>[] = [];
  await page.route(`**/v1/portal/surveys/${invitation.id}/answer`, route => { answers.push(route.request().postDataJSON()); return route.fulfill({ status: 204 }); });
  await page.goto(`/surveys/${invitation.id}`);
  await page.getByRole("radio").last().check();
  await page.getByLabel("Your explanation (Optional)").fill("Handling was good");
  await page.getByRole("button", { name: "Answer registration", exact: true }).click();
  await expect.poll(() => answers.length).toBe(1);
  expect(answers[0]).toEqual({ score: 5, comment: "Handling was good" });
  await expect(page.getByRole("status").filter({ hasText: "Your answer has been recorded" })).toBeVisible();
});
for (const path of ["tracking", "surveys", "notifications"]) {
  test(`${path} fits a mobile screen`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/${path}`);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`${path}-mobile.png`), fullPage: true });
  });
}
