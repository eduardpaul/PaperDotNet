import { expect, test } from '@playwright/test';
import { adminHeaders, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

test('an administrator manages users, groups and roles', async ({ page }) => {
  test.setTimeout(60_000);
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Administration' }).click();
  await expect(page.getByRole('heading', { name: 'Administration' })).toBeVisible();

  // Users: create one, then turn the account off and on again.
  await page.getByRole('link', { name: 'Users' }).click();
  const userName = unique('clerk').replaceAll(' ', '-');
  const displayName = `Clerk ${userName}`;
  await page.getByRole('button', { name: 'New user' }).click();
  let dialog = page.getByRole('dialog');
  await dialog.getByLabel('User name').fill(userName);
  await dialog.getByLabel('Display name').fill(displayName);
  await dialog.getByLabel('Password').fill('clerk-password-1');
  await dialog.getByRole('button', { name: 'Create user' }).click();
  await expect(page.getByText('User created.')).toBeVisible();
  await page.getByLabel('Find users').fill(userName);
  const userRow = page.getByRole('listitem').filter({ hasText: displayName });
  await expect(userRow).toBeVisible();
  await userRow.getByRole('button', { name: `Edit ${userName}` }).click();
  await page
    .getByRole('dialog')
    .getByLabel(/^Disabled/)
    .check();
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect(userRow).toContainText('Disabled');
  await userRow.getByRole('button', { name: `Edit ${userName}` }).click();
  await page
    .getByRole('dialog')
    .getByLabel(/^Disabled/)
    .uncheck();
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect(userRow).not.toContainText('Disabled');

  // Groups: create one, add the user and a group inside it, rename it.
  await page.getByRole('link', { name: 'Groups' }).click();
  const inner = unique('Interns');
  await page.getByRole('button', { name: 'New group' }).click();
  await page.getByRole('dialog').getByLabel('Name').fill(inner);
  await page.getByRole('button', { name: 'Create group' }).click();
  await expect(page.getByRole('button', { name: inner, exact: true })).toBeVisible();
  const group = unique('Clerks');
  await page.getByRole('button', { name: 'New group' }).click();
  await page.getByRole('dialog').getByLabel('Name').fill(group);
  await page.getByRole('button', { name: 'Create group' }).click();
  await expect(page.getByRole('button', { name: group, exact: true })).toBeVisible();
  await page.getByRole('button', { name: group, exact: true }).click();
  await page.getByRole('combobox', { name: `Add members to ${group}` }).click();
  await page.getByRole('option', { name: new RegExp(displayName) }).click();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('button', { name: `Remove ${displayName} from ${group}` })).toBeVisible();
  // A group inside the group: its members count as members.
  await page.getByRole('combobox', { name: 'Groups inside' }).selectOption({ label: inner });
  await expect(page.getByRole('list', { name: 'Groups inside' }).getByText(inner)).toBeVisible();
  const renamed = `${group} renamed`;
  await page.getByRole('button', { name: `Edit ${group}` }).click();
  await page.getByRole('dialog').getByLabel('Name').fill(renamed);
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Group saved.')).toBeVisible();
  await expect(page.getByRole('button', { name: renamed, exact: true })).toBeVisible();

  // Roles: a custom role with one scope, given to the user and taken away again.
  await page.getByRole('link', { name: 'Roles' }).click();
  const role = unique('Clerks role');
  await page.getByRole('button', { name: 'New role' }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name', { exact: true }).fill(role);
  await dialog.getByRole('checkbox', { name: /user\.read/ }).check();
  await dialog.getByRole('button', { name: 'Create role' }).click();
  await expect(page.getByText('Role created.')).toBeVisible();
  await page.getByRole('button', { name: new RegExp(`^${role}`) }).click();
  await page.getByRole('combobox', { name: `Give ${role} to` }).click();
  await page.getByRole('option', { name: new RegExp(displayName) }).click();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Give role' }).click();
  const take = page.getByRole('button', { name: `Take ${role} from ${displayName}` });
  await expect(take).toBeVisible();
  await take.click();
  await expect(page.getByText('Nobody has this role.')).toBeVisible();
  await page.getByRole('button', { name: `Delete ${role}` }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete role' }).click();
  await expect(page.getByText('Role deleted.')).toBeVisible();

  // Every change is in the audit log.
  await page.getByRole('link', { name: 'Audit log' }).click();
  await page.getByLabel('What').fill('identity.Role');
  await expect(
    page
      .getByRole('row')
      .filter({ hasText: /purged identity\.Role/ })
      .first(),
  ).toBeVisible();
});

test('the term store holds groups, sets and terms, promotes keywords and imports CSV', async ({ page, request }) => {
  test.setTimeout(60_000);
  const keyword = unique('tag').replaceAll(' ', '-');
  const created = await request.post('/v1.0/termStore/keywords', {
    headers: await adminHeaders(request),
    data: { name: keyword },
  });
  expect(created.ok()).toBeTruthy();

  await page.goto('/admin/terms');
  const group = unique('Places');
  await page.getByRole('button', { name: 'Group', exact: true }).click();
  await page.getByRole('dialog').getByLabel('Name').fill(group);
  await page.getByRole('dialog').getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(group)).toBeVisible();

  const set = unique('Regions');
  await page.getByRole('button', { name: `New term set in ${group}` }).click();
  await page.getByRole('dialog').getByLabel('Name').fill(set);
  await page.getByRole('dialog').getByRole('button', { name: 'Create' }).click();
  await page.getByRole('button', { name: set }).click();
  await expect(page.getByRole('heading', { name: set })).toBeVisible();
  await page.getByRole('button', { name: 'New term', exact: true }).click();
  await page.getByRole('dialog').getByLabel('Name').fill('Europe');
  await page.getByRole('dialog').getByLabel('Synonyms').fill('EU');
  await page.getByRole('dialog').getByRole('button', { name: 'Create term' }).click();
  await page.getByRole('button', { name: 'Add a term under Europe' }).click();
  await page.getByRole('dialog').getByLabel('Name').fill('Germany');
  await page.getByRole('dialog').getByRole('button', { name: 'Create term' }).click();
  await page.getByRole('button', { name: 'Expand Europe' }).click();
  await expect(page.getByText('Germany')).toBeVisible();

  // Free keywords are promoted into a managed term set.
  await page.getByRole('button', { name: /^Keywords/ }).click();
  await page.getByLabel('Promote into').selectOption({ label: set });
  await page.getByRole('button', { name: `Promote ${keyword}` }).click();
  await expect(page.getByText(`“${keyword}” promoted.`)).toBeVisible();

  // A SharePoint CSV makes a new term set.
  const imported = unique('Countries');
  await page.getByRole('button', { name: `Import terms into ${group}` }).click();
  await page.getByLabel('CSV file').setInputFiles({
    name: 'terms.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from(
      ['Term Set Name,Term Set Description,Level 1 Term,Level 2 Term', `${imported},Imported,Europe,France`].join('\n'),
    ),
  });
  await expect(page.getByLabel('CSV', { exact: true })).toHaveValue(/France/);
  await page.getByRole('dialog').getByRole('button', { name: 'Import' }).click();
  await expect(page.getByText(/terms imported into a new term set/)).toBeVisible();
  await expect(page.getByRole('button', { name: imported })).toBeVisible();
});

test('organization defaults, extensions, applications and maintenance', async ({ page }) => {
  test.setTimeout(60_000);
  await page.goto('/admin');
  await expect(page.getByText('Built-in default').first()).toBeAttached();
  await page.getByLabel('Time format').selectOption('12h');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Organization defaults saved.')).toBeVisible();
  await page.reload();
  await expect(page.getByLabel('Time format')).toHaveValue('12h');
  await page.getByLabel('Time format').selectOption('');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Organization defaults saved.')).toBeVisible();

  await page.getByRole('link', { name: 'Extensions' }).click();
  await expect(page.getByRole('heading', { name: 'Extensions' }).or(page.getByRole('region')).first()).toBeVisible();

  // A confidential application gets a secret shown once.
  await page.getByRole('link', { name: 'Applications' }).click();
  const app = unique('Scanner');
  await page.getByRole('button', { name: 'New application' }).click();
  await page.getByRole('dialog').getByLabel('Name').fill(app);
  await page.getByRole('dialog').getByLabel('Redirect addresses').fill('https://scanner.example.com/callback');
  await page
    .getByRole('dialog')
    .getByRole('button', { name: /^Create/ })
    .click();
  await expect(page.getByText('Application created.')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Client secret' })).not.toHaveValue('');

  await page.getByRole('dialog').getByRole('button', { name: 'Done' }).click();
  await page.getByRole('link', { name: 'Maintenance' }).click();
  await page.getByRole('button', { name: 'Rebuild the index' }).click();
  await expect(page.getByRole('status', { name: 'Rebuilding the search index' })).toContainText('succeeded', {
    timeout: 30_000,
  });
  const download = page.waitForEvent('download');
  await page.getByRole('region', { name: 'Templates' }).getByRole('button', { name: 'Download' }).click();
  expect((await download).suggestedFilename()).toBe('paperdotnet-template.xml');
});
