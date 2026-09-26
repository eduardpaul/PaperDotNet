import { expect, test } from '@playwright/test';
import { signIn, unique } from './helpers';

// Every screen loads without console errors (including Content-Security-Policy violations).
let errors: string[] = [];
test.beforeEach(async ({ page }) => {
  errors = [];
  // Failed requests are reported with their address (the console only says "Failed to load resource").
  page.on('response', (response) => {
    if (response.status() >= 400) errors.push(`${response.status()} ${response.request().method()} ${response.url()}`);
  });
  page.on('console', (message) => {
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource'))
      errors.push(message.text());
  });
  page.on('pageerror', (error) => errors.push(error.message));
  await signIn(page);
});
test.afterEach(() => expect(errors).toEqual([]));

test('Home shows today with tasks, agenda and workspaces', async ({ page }) => {
  await expect(page.getByRole('heading', { name: /Good (morning|afternoon|evening), / })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'My tasks' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Agenda' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'This week' })).toHaveAttribute('aria-selected', 'true');
});

test('a new workspace appears in the sidebar and opens', async ({ page }) => {
  const name = unique('Finance');
  await page.getByRole('link', { name: 'New workspace' }).click();
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Description').fill('Invoices and contracts');
  await page.getByRole('button', { name: 'Create workspace' }).click();

  await expect(page.getByRole('heading', { name })).toBeVisible();
  await expect(page.getByText('Invoices and contracts')).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name })).toBeVisible();
});

test('the command palette jumps to screens and workspaces', async ({ page }) => {
  await page.keyboard.press('ControlOrMeta+k');
  const palette = page.getByRole('dialog');
  await palette.getByRole('combobox').fill('notifications');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Notifications' })).toBeVisible();

  // "g h" goes Home.
  await page.keyboard.press('g');
  await page.keyboard.press('h');
  await expect(page.getByRole('heading', { name: 'My tasks' })).toBeVisible();
});

test('the theme follows the user preference and is kept, before the first paint', async ({ page }) => {
  await page.getByRole('button', { name: 'Account' }).click();
  await page.getByRole('menuitemradio', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveClass(/dark/);

  // theme.js applies it from the stored preference before the app loads (allowed by the CSP: no inline script).
  await page.route('**/v1.0/me/preferences', (route) => route.abort());
  await page.reload();
  await expect(page.locator('html')).toHaveClass(/dark/);
  await page.unroute('**/v1.0/me/preferences');
  errors = errors.filter((e) => !e.includes('net::ERR_FAILED'));
  await page.reload();

  await page.getByRole('button', { name: 'Account' }).click();
  await page.getByRole('menuitemradio', { name: 'Light' }).click();
  await expect(page.locator('html')).not.toHaveClass(/dark/);
});

test('the navigation opens as a drawer on phones @phone', async ({ page, isMobile }) => {
  test.skip(!isMobile, 'phone layout only');
  await page.getByRole('button', { name: 'Open navigation' }).click();
  await page.getByRole('dialog').getByRole('link', { name: 'Notifications' }).click();
  await expect(page.getByRole('heading', { name: 'Notifications' })).toBeVisible();
  await expect(page.getByRole('dialog')).toBeHidden();
});
