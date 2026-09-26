import { expect, type Page } from '@playwright/test';

export const adminUser = 'admin';
export const adminPassword = process.env.PAPERDOTNET_ADMIN_PASSWORD ?? 'admin-password-e2e';

/** A name no other test uses. */
export function unique(prefix: string): string {
  return `${prefix} ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}

/** Signs in through the UI: the app sends the browser to the server's authorization endpoint, which shows /login. */
export async function signIn(page: Page, path = '/', userName = adminUser, password = adminPassword) {
  await page.goto(path);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.getByLabel('User name or email').fill(userName);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Account' })).toBeVisible();
}
