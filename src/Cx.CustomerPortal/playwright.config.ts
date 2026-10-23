import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  timeout: 30000,
  workers: 2,
  fullyParallel: true,
  use: {
    baseURL: "http://127.0.0.1:5174",
    channel: process.env.PW_CHANNEL || undefined,
    headless: true,
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
    permissions: ["microphone"],
    launchOptions: {
      executablePath: process.env.PW_EXECUTABLE_PATH || undefined,
      // A synthetic microphone, so the voice-message flow can be recorded headless.
      args: [
        "--use-fake-ui-for-media-stream",
        "--use-fake-device-for-media-stream",
      ],
    },
  },
  webServer: {
    command: "node node_modules/vite/bin/vite.js --host 127.0.0.1",
    url: "http://127.0.0.1:5174",
    reuseExistingServer: true,
  },
});
