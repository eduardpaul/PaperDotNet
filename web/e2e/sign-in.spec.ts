import { expect, test } from '@playwright/test';
import { adminPassword, signIn } from './helpers';

test('signed-out visitors sign in and return where they were going', async ({ page }) => {
  await page.goto('/notifications');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fconnect%2Fauthorize/);

  await page.getByLabel('User name or email').fill('admin');
  await page.getByLabel('Password').fill(adminPassword);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page).toHaveURL(/\/notifications$/);
  await expect(page.getByRole('heading', { name: 'Notifications' })).toBeVisible();
});

test('a wrong password is explained and nothing else happens', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('User name or email').fill('admin');
  await page.getByLabel('Password').fill('not-the-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page.getByRole('alert')).toContainText('incorrect');
  await expect(page).toHaveURL(/\/login/);
});

test('the session survives a reload and a new tab, and signing out ends it', async ({ page, context }) => {
  await signIn(page);
  await page.reload();
  await expect(page.getByRole('heading', { name: /Good (morning|afternoon|evening)/ })).toBeVisible();

  // A new tab has no tokens yet, but the server's sign-in session signs it in without a password.
  const second = await context.newPage();
  await second.goto('/w');
  await expect(second.getByRole('heading', { name: 'Workspaces' })).toBeVisible();
  await second.close();

  await page.getByRole('button', { name: 'Account' }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.getByText('You have been signed out.')).toBeVisible();

  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});
