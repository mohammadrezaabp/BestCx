// Screenshots of the reworked screens against a running stack:
//   node tests/new-screens.mjs [baseUrl] [outDir]
import { chromium } from "@playwright/test";

const base = process.argv[2] ?? "http://localhost:8080";
const out = process.argv[3] ?? "test-results";
// PW_CHANNEL=msedge (or chrome) uses an installed browser instead of a downloaded one.
const browser = await chromium.launch({ channel: process.env.PW_CHANNEL || undefined });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" });
const errors = [];
page.on("pageerror", (error) => errors.push(error.message));
page.on("console", (message) => message.type() === "error" && errors.push(message.text()));

async function shot(name) {
  await page.waitForLoadState("networkidle");
  await page.screenshot({ path: `${out}/${name}.png`, fullPage: false });
}

await page.goto(`${base}/inbox`);
await page.locator(".data-grid tbody tr").first().waitFor();
await shot("inbox");
await page.locator(".data-grid tbody tr").first().click();
await page.locator(".analysis-box").waitFor();
await page.locator("audio.detail-audio, .analysis-labels").first().waitFor();
await page.waitForTimeout(600);
await shot("inbox-drawer");
await page.keyboard.press("Escape");

await page.goto(`${base}/customers`);
await page.locator(".data-grid tbody tr").first().waitFor();
await shot("customers");
await page.getByRole("button", { name: "New customer" }).click();
await page.locator("dialog.modal").waitFor();
await shot("customer-new");
await page.keyboard.press("Escape");

await page.locator(".customer-cell").first().click();
await page.locator(".profile-card").waitFor();
await shot("customer-360");

await page.getByRole("link", { name: "Audio feedback" }).click();
await page.locator(".voice-choices").waitFor();
await shot("collect-voice");

await page.setViewportSize({ width: 400, height: 860 });
await page.goto(`${base}/inbox`);
await page.locator(".data-grid tbody tr").first().waitFor();
await shot("inbox-mobile");

console.log(errors.length ? `console errors:\n${errors.join("\n")}` : "no console errors");
await browser.close();
