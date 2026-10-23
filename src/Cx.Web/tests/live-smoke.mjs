import { chromium } from '@playwright/test';
const baseURL = process.env.CX_WEB_URL || 'http://127.0.0.1:5173';
const browser = await chromium.launch({ channel: process.env.PW_CHANNEL || 'msedge', headless: true });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => { if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) errors.push(message.text()); });
const paths = ['/', '/inbox', '/inbox?status=new', '/voice', '/ai-review', '/customers', '/taxonomy', '/pipelines', '/audit'];
const calls = await (await page.request.get(baseURL + '/v1/voice/calls?limit=1')).json();
const customers = await (await page.request.get(baseURL + '/v1/customers?limit=1')).json();
if (calls.items[0]) paths.push(`/voice/${encodeURIComponent(calls.items[0].call_id)}`);
if (customers.items[0]) paths.push(`/customers/${encodeURIComponent(customers.items[0].customer_id)}`);
const checks = [];
for (const width of [1440, 390]) {
  await page.setViewportSize({ width, height: 1000 });
  for (const path of paths) {
    await page.goto(`${baseURL}${path}`);
    await page.waitForLoadState('networkidle');
    checks.push({ path, width, heading: await page.locator('h1').textContent(), overflow: await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), errors: await page.getByRole('alert').allTextContents() });
  }
}
console.log(JSON.stringify({ checks, errors }, null, 2));
await browser.close();
if (errors.length || checks.some(check => check.overflow || check.errors.length)) process.exitCode = 1;
