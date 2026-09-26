import { expect, test, type Page } from '@playwright/test';
import { createList, signIn, unique } from './helpers';

/** Creates an API token with the given scopes on the tokens page and returns its secret. */
async function createToken(page: Page, name: string, scopes: string[]) {
  await page.goto('/settings/tokens');
  await page.getByRole('button', { name: 'New token' }).click();
  const dialog = page.getByRole('dialog', { name: 'New API token' });
  await dialog.getByLabel('Name', { exact: true }).fill(name);
  for (const scope of scopes)
    await dialog.getByRole('checkbox', { name: new RegExp(`^${scope.replace('.', '\\.')}\\b`) }).check();
  await dialog.getByRole('button', { name: 'Create token' }).click();
  const secret = await page.getByRole('textbox', { name: 'API token' }).inputValue();
  expect(secret).toMatch(/^pdn_/);
  await page.getByRole('button', { name: 'Done' }).click();
  return secret;
}

test.describe('as the administrator', () => {
  test.beforeEach(async ({ page }) => signIn(page));

  test('the profile and preferences change how the app shows things', async ({ page }) => {
    await page.getByRole('button', { name: 'Account' }).click();
    await page.getByRole('menuitem', { name: 'Settings' }).click();
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();

    const name = page.getByLabel('Display name');
    const original = await name.inputValue();
    const changed = unique('Admin');
    await name.fill(changed);
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('Profile saved.')).toBeVisible();
    await page.getByRole('button', { name: 'Account' }).click();
    await expect(page.getByRole('menu').getByText(changed)).toBeVisible();
    await page.keyboard.press('Escape');
    await name.fill(original);
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(name).toHaveValue(original);

    await page.getByRole('link', { name: 'Preferences' }).click();
    const dateFormat = page.getByLabel('Date format');
    await dateFormat.selectOption('dd.MM.yyyy');
    await expect(page.getByText(/Today: \d{2}\.\d{2}\.\d{4}/)).toBeVisible();
    await expect(page.getByText('Unsaved changes')).toBeVisible();
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('Preferences saved.')).toBeVisible();
    await page.reload();
    await expect(dateFormat).toHaveValue('dd.MM.yyyy');

    // Back to the organization's default.
    await dateFormat.selectOption('');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('Unsaved changes')).toBeHidden();
    await page.reload();
    await expect(dateFormat).toHaveValue('');
  });

  test('API tokens work for scripts until revoked, and new users change their password', async ({
    page,
    request,
    browser,
  }) => {
    const secret = await createToken(page, unique('Script'), ['user.manage', 'user.read']);
    const headers = { Authorization: `Bearer ${secret}` };
    const me = await request.get('/v1.0/me', { headers });
    expect(me.status()).toBe(200);
    expect((await me.json()).userName).toBe('admin');

    const userName = unique('user').replace(' ', '-');
    const created = await request.post('/v1.0/users', {
      headers,
      data: { userName, password: 'first-password-1' },
    });
    expect(created.status()).toBe(201);

    const row = page.getByRole('listitem').filter({ has: page.getByText(/^Script /) });
    await row.getByRole('button', { name: /^Revoke/ }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Revoke' }).click();
    await expect(page.getByText('Token revoked.')).toBeVisible();
    expect((await request.get('/v1.0/me', { headers })).status()).toBe(401);

    // The new user, in their own browser.
    const context = await browser.newContext();
    const user = await context.newPage();
    await signIn(user, '/settings/security', userName, 'first-password-1');
    await user.getByLabel('Current password').fill('not-my-password');
    await user.getByLabel('New password', { exact: true }).fill('second-password-2');
    await user.getByLabel('Repeat new password').fill('second-password-2');
    await user.getByRole('button', { name: 'Change password' }).click();
    await expect(user.getByRole('alert')).toBeVisible();
    await expect(user.getByLabel('Current password')).toHaveAttribute('aria-invalid', 'true');

    await user.getByLabel('Current password').fill('first-password-1');
    await user.getByRole('button', { name: 'Change password' }).click();
    await expect(user.getByText(/Password changed/)).toBeVisible();

    await user.getByRole('button', { name: 'Account' }).click();
    await user.getByRole('menuitem', { name: 'Sign out' }).click();
    await expect(user.getByText('You have been signed out.')).toBeVisible();
    await signIn(user, '/', userName, 'second-password-2');
    await context.close();
  });

  test('a passkey is added, signs in without a password and is removed', async ({ page }) => {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', {
      options: {
        protocol: 'ctap2',
        transport: 'internal',
        hasResidentKey: true,
        hasUserVerification: true,
        isUserVerified: true,
        automaticPresenceSimulation: true,
      },
    });

    await page.goto('/settings/security');
    await page.getByRole('button', { name: 'Add a passkey' }).click();
    const name = unique('Laptop');
    await page.getByRole('dialog').getByLabel('Name').fill(name);
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.getByText('Passkey added.', { exact: false })).toBeVisible();
    await expect(page.getByText(name)).toBeVisible();

    await page.getByRole('button', { name: 'Account' }).click();
    await page.getByRole('menuitem', { name: 'Sign out' }).click();
    await expect(page.getByText('You have been signed out.')).toBeVisible();
    await page.goto('/settings/security');
    await page.getByRole('button', { name: 'Sign in with a passkey' }).click();
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();

    await page.getByRole('button', { name: `Remove ${name}` }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Remove' }).click();
    await expect(page.getByText('Passkey removed.')).toBeVisible();
    await expect(page.getByText(name)).toBeHidden();
  });

  test('notification channels, quiet hours, the webhook and followed items are managed in one place', async ({
    page,
  }) => {
    await page.goto('/settings/notifications');
    await page.getByRole('button', { name: 'Send a test' }).click();
    await expect(page.getByText(/Test notification sent/)).toBeVisible();

    const mentions = page.getByRole('checkbox', { name: 'Mentions in app' });
    await mentions.uncheck();
    await page.getByLabel('Quiet hours').check();
    await page.getByLabel('Quiet from').fill('21:30');
    await page.getByLabel('Quiet until').fill('06:45');
    await page.getByLabel('Daily digest at').selectOption('8');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('Notification settings saved.')).toBeVisible();
    await page.reload();
    await expect(mentions).not.toBeChecked();
    await expect(page.getByLabel('Quiet from')).toHaveValue('21:30');
    await expect(page.getByLabel('Daily digest at')).toHaveValue('8');

    await page.getByLabel('Webhook', { exact: true }).fill('https://example.com/paperdotnet-hook');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('textbox', { name: 'Webhook secret' })).toHaveValue(/^whsec_/);

    // Back to the defaults for the other tests.
    await mentions.check();
    await page.getByLabel('Quiet hours').uncheck();
    await page.getByLabel('Webhook', { exact: true }).fill('');
    await page.getByLabel('Daily digest at').selectOption('7');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('Unsaved changes')).toBeHidden();

    await page.getByRole('link', { name: 'Notifications', exact: true }).first().click();
    await expect(page.getByText('Test notification').first()).toBeVisible();

    // Following: an item followed from its panel is listed with how often, and can be unfollowed.
    await createList(page, 'Tasks', 'Followed');
    const title = unique('Watch me');
    await page.getByRole('button', { name: 'New task' }).click();
    const panel = page.getByRole('dialog');
    await panel.getByLabel('Title').fill(title);
    await panel.getByRole('button', { name: 'Create' }).click();
    await panel.getByRole('button', { name: 'Follow', exact: true }).click();
    await expect(panel.getByRole('button', { name: 'Following' })).toBeVisible();
    await page.goto('/settings/notifications');
    const often = page.getByLabel(`How often for ${title}`);
    await often.selectOption('daily');
    await page.reload();
    await expect(often).toHaveValue('daily');
    await page.getByRole('listitem').filter({ hasText: title }).getByRole('button', { name: 'Unfollow' }).click();
    await expect(page.getByText(`You no longer follow “${title}”.`)).toBeVisible();
    await expect(often).toBeHidden();
  });

  test('a calendar feed gives other apps a private .ics address', async ({ page, request }) => {
    await page.goto('/settings/calendar-feeds');
    await page.getByRole('button', { name: 'New feed' }).click();
    const name = unique('Phone');
    await page.getByRole('dialog').getByLabel('Name').fill(name);
    await page.getByRole('button', { name: 'Create feed' }).click();
    const url = await page.getByRole('textbox', { name: 'Feed address' }).inputValue();
    expect(url).toMatch(/\/v1\.0\/calendarFeeds\/.+\.ics$/);
    const feed = await request.get(url);
    expect(feed.status()).toBe(200);
    expect(await feed.text()).toContain('BEGIN:VCALENDAR');
    await page.getByRole('button', { name: 'Done' }).click();

    await page.getByRole('button', { name: `Delete ${name}` }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click();
    await expect(page.getByText('Feed deleted.')).toBeVisible();
    await expect(page.getByText(name)).toBeHidden();
    expect((await request.get(url)).status()).toBe(404);
  });
});
