import { expect, test } from '@playwright/test';
import { adminHeaders, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

test('an administrator manages people, groups and roles', async ({ page }) => {
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Administration' }).click();
  await expect(page.getByRole('heading', { name: 'Administration' })).toBeVisible();

  const userName = unique('clerk').replaceAll(' ', '-');
  const displayName = `Clerk ${userName}`;
  await page.getByLabel('User name').fill(userName);
  await page.getByLabel('Initial password').fill('clerk-password-1');
  await page.getByLabel('Display name').fill(displayName);
  await page.getByRole('button', { name: 'Create user' }).click();
  await expect(page.getByText('User created.')).toBeVisible();
  const userRow = page.getByRole('listitem').filter({ hasText: userName });
  await expect(userRow).toBeVisible();

  await userRow.getByRole('button', { name: 'Disable' }).click();
  await expect(userRow).toContainText('Disabled');
  await userRow.getByRole('button', { name: 'Enable' }).click();
  await expect(userRow).not.toContainText('Disabled');

  await page.getByRole('tab', { name: 'Groups' }).click();
  const group = unique('Clerks');
  await page.getByLabel('Name', { exact: true }).fill(group);
  await page.getByRole('button', { name: 'Create group' }).click();
  await expect(page.getByText('Group created.')).toBeVisible();
  const groupItem = page.getByRole('listitem').filter({ hasText: group });
  await groupItem.getByRole('button', { name: group }).click();
  await groupItem.getByLabel('Add a member').selectOption({ label: displayName });
  await expect(groupItem.getByRole('listitem').filter({ hasText: displayName })).toBeVisible();
  const renamed = `${group} renamed`;
  await groupItem.getByLabel('Name').fill(renamed);
  await groupItem.getByRole('button', { name: 'Rename' }).click();
  await expect(page.getByText('Group renamed.')).toBeVisible();

  await page.getByRole('tab', { name: 'Roles' }).click();
  const role = unique('Clerks role');
  await page.getByLabel('Name', { exact: true }).fill(role);
  await page.getByRole('checkbox', { name: /user\.read/ }).check();
  await page.getByRole('button', { name: 'Create role' }).click();
  await expect(page.getByText('Role created.')).toBeVisible();
  await page.getByLabel('Role', { exact: true }).selectOption(role);
  await page.getByLabel('Person').selectOption({ label: displayName });
  await page.getByRole('button', { name: 'Assign' }).click();
  await expect(page.getByText('Role assigned.')).toBeVisible();
  const assignment = page.getByRole('listitem').filter({ hasText: displayName });
  await expect(assignment).toBeVisible();
  await assignment.getByRole('button', { name: 'Remove' }).click();
  await expect(page.getByText('Nobody has this role yet.')).toBeVisible();

  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete role' }).click();
  await expect(page.getByText('Role deleted.')).toBeVisible();
});

test('keywords can be promoted and a term set imported from CSV', async ({ page, request }) => {
  test.setTimeout(45_000);
  const keyword = unique('tag').replaceAll(' ', '-');
  const created = await request.post('/v1.0/termStore/keywords', {
    headers: await adminHeaders(request),
    data: { name: keyword },
  });
  expect(created.ok()).toBeTruthy();

  await page.goto('/admin/terms');
  const group = unique('Places');
  await page.getByRole('region', { name: 'New group' }).getByLabel('Name').fill(group);
  await page.getByRole('button', { name: 'Create group' }).click();
  await expect(page.getByText('Term group created.')).toBeVisible();

  const set = unique('Regions');
  await page.getByLabel('Group', { exact: true }).selectOption(group);
  await page.getByRole('region', { name: 'New term set' }).getByLabel('Name').fill(set);
  await page.getByRole('button', { name: 'Create term set' }).click();
  await expect(page.getByText('Term set created.')).toBeVisible();

  await page.getByLabel('Promote into').selectOption(set);
  await page.getByRole('button', { name: `Promote ${keyword}` }).click();
  await expect(page.getByText(`Promoted “${keyword}”.`)).toBeVisible();

  const csv = [
    'Term Set Name,Term Set Description,Level 1 Term,Level 2 Term',
    `Countries,Imported,Europe,Germany`,
  ].join('\n');
  await page.getByLabel('Import into').selectOption(group);
  await page.getByLabel('Term set CSV').setInputFiles({
    name: 'terms.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from(csv),
  });
  await expect(page.getByText(/Term set imported with \d+ terms/)).toBeVisible();
  await expect(page.getByRole('listitem').filter({ hasText: /^Countries$/ })).toBeVisible();
});

test('organization defaults and a provisioning template are available', async ({ page }) => {
  await page.goto('/admin/organization');
  await expect(page.getByRole('heading', { name: 'Defaults' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save defaults' })).toBeDisabled();

  await page.getByRole('link', { name: 'Maintenance' }).click();
  await expect(page.getByRole('heading', { name: 'Search index' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Provisioning' })).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Download template' }).click();
  expect((await download).suggestedFilename()).toBe('tenant-template.xml');
  await expect(page.getByText('Template downloaded.')).toBeVisible();
});
