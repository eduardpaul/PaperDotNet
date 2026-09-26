import { defineConfig, devices } from '@playwright/test';

// End-to-end tests against a real host serving the built UI (e2e/server.mjs). PAPERDOTNET_WEB_URL runs them against
// a server that is already running instead. PLAYWRIGHT_CHROMIUM_PATH uses an installed Chromium.
const port = process.env.PAPERDOTNET_E2E_PORT ?? '5199';
const baseURL = process.env.PAPERDOTNET_WEB_URL ?? `http://localhost:${port}`;

export default defineConfig({
  testDir: 'e2e',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 30_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH } : {},
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] }, grepInvert: /@phone/ },
    { name: 'phone', use: { ...devices['Pixel 7'] }, grep: /@phone/ },
  ],
  webServer: process.env.PAPERDOTNET_WEB_URL
    ? undefined
    : {
        command: 'node e2e/server.mjs',
        url: `${baseURL}/health/ready`,
        timeout: 180_000,
        reuseExistingServer: false,
        stdout: 'ignore',
        stderr: 'pipe',
        gracefulShutdown: { signal: 'SIGTERM', timeout: 15_000 },
      },
});
