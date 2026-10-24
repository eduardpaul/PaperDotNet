import { test, expect } from '@playwright/test';
import { signIn, unique } from './helpers';

test('content drafts, schema snapshots, history, publishing and archive', async ({ page }) => {
  await signIn(page, '/content');
  const workspace = unique('Content workspace');
  await page.getByRole('button', { name: 'Workspace', exact: true }).click();
  await page.getByRole('dialog').getByLabel('Name', { exact: true }).fill(workspace);
  await page.getByRole('dialog').getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page.getByLabel('Workspace', { exact: true })).toHaveText(new RegExp(workspace));
  await page.getByRole('button', { name: 'New collection' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name', { exact: true }).fill('Records');
  await dialog.getByLabel('Require publishing').check();
  await dialog.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Records', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await dialog.getByLabel('Keep metadata history').check();
  await dialog.getByRole('button', { name: 'Save lifecycle settings' }).click();
  await expect(dialog.getByRole('button', { name: 'Save lifecycle settings' })).toBeEnabled();
  await dialog.getByLabel('Field label').fill('Reference');
  await dialog.getByLabel('Field type').selectOption('integer');
  await dialog.getByRole('button', { name: 'Add field', exact: true }).click();
  await expect(dialog.getByRole('listitem').filter({ hasText: 'Reference' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('button', { name: 'New item', exact: true }).click();
  await dialog.getByLabel('Name', { exact: true }).fill('Original draft');
  await dialog.getByLabel('Reference', { exact: true }).fill('9223372036854775807');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Original draft', exact: true })).toBeVisible();
  await expect(page.getByText('9223372036854775807', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Publish', exact: true })).toBeDisabled();
  await page.getByLabel('Content surface').selectOption('published');
  await page.getByRole('button', { name: /Original draft/ }).click();
  await page.getByRole('button', { name: 'Edit details', exact: true }).click();
  await dialog.getByLabel('Name', { exact: true }).fill('Revised draft');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Original draft', exact: true })).toBeVisible();
  await page.getByLabel('Content surface').selectOption('head');
  await page.getByRole('button', { name: /Revised draft/ }).click();
  await expect(page.getByRole('heading', { name: 'Revised draft', exact: true })).toBeVisible();
  await expect(page.getByText('9223372036854775807', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'History', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Content history' })).toContainText('Original draft');
  await page.getByRole('button', { name: 'Archive', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Unarchive', exact: true })).toBeVisible();
  await page.getByLabel('Archive', { exact: true }).selectOption('archived');
  await page.getByLabel('Collection', { exact: true }).selectOption('all');
  await page.getByRole('button', { name: /Revised draft/ }).click();
  await page.getByRole('button', { name: 'Unarchive', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Archive', exact: true })).toBeVisible();
  await page.getByLabel('Archive', { exact: true }).selectOption('active');
  await expect(page.getByRole('button', { name: /Revised draft/ })).toBeVisible();
});

test('existing workspace import previews the copy and opens preserved content', async ({ page, request }) => {
  const { adminHeaders } = await import('./helpers');
  const headers = await adminHeaders(request);
  const name = unique('Legacy workspace');
  const wsResponse = await request.post('/v1.0/workspaces', { headers, data: { name } });
  expect(wsResponse.ok()).toBeTruthy();
  const workspace = (await wsResponse.json()).id as string;
  const typeResponse = await request.post('/v1.0/contentTypes', {
    headers,
    data: { name: unique('Record type'), fields: [{ name: 'status', displayName: 'Status', type: 'text' }] },
  });
  expect(typeResponse.ok()).toBeTruthy();
  const type = (await typeResponse.json()).id as string;
  const listResponse = await request.post(`/v1.0/workspaces/${workspace}/lists`, {
    headers,
    data: { name: 'Imported records', contentTypeIds: [type], versioning: 'major' },
  });
  expect(listResponse.ok()).toBeTruthy();
  const list = (await listResponse.json()).id as string;
  const itemResponse = await request.post(`/v1.0/workspaces/${workspace}/lists/${list}/items`, {
    headers,
    data: { fields: { title: 'Preserved item', status: 'Reviewed' } },
  });
  expect(itemResponse.ok()).toBeTruthy();
  await signIn(page, '/content');
  await page.getByRole('button', { name: 'Import workspace', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Source workspace').selectOption(workspace);
  await dialog.getByRole('button', { name: 'Preview import', exact: true }).click();
  await expect(dialog.getByText(/1 collections · 1 items/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Import workspace', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Imported records', exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Preserved item/ }).click();
  await expect(page.getByText('Reviewed', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit details', exact: true }).click();
  await dialog.getByLabel('Name', { exact: true }).fill('Edited after import');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Edited after import', exact: true })).toBeVisible();
});
