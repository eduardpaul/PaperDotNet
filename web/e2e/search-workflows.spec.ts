import { expect, test } from '@playwright/test';
import { adminHeaders, createList, signIn, unique } from './helpers';

test('items expose indexing status, manual runs and immediate search exclusion', async ({ page, request }) => {
  test.setTimeout(90_000);
  await signIn(page);
  const headers = await adminHeaders(request);
  const listUrl = await createList(page, 'Tasks', unique('Indexed tasks'));
  const workspaceId = /\/w\/([^/]+)/.exec(listUrl)![1];
  const listId = /\/l\/([^/]+)/.exec(listUrl)![1];
  const root = `/v1.0/workspaces/${workspaceId}/lists/${listId}`;
  const builtIns = await (await request.get(`${root}/workflows/builtIns`, { headers })).json();
  const indexing = builtIns.find((workflow: { key: string }) => workflow.key === 'search.index');
  expect(
    (
      await request.put(`${root}/workflows/builtIns/search.index`, {
        headers: { ...headers, 'If-Match': indexing['@odata.etag'] },
        data: { enabled: false },
      })
    ).ok(),
  ).toBeTruthy();
  const title = unique('Indexable');
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(title);
  await panel.getByRole('button', { name: 'Create' }).click();
  await panel.getByRole('tab', { name: 'Workflows', exact: true }).click();
  const indexingSection = panel.getByRole('region', { name: 'Search indexing' });
  await expect(indexingSection).toContainText('Not indexed');
  await indexingSection.getByRole('button', { name: 'Index now' }).click();
  await expect(indexingSection).toContainText('Indexed', { timeout: 30_000 });
  await expect(panel.getByRole('region', { name: 'Workflow history' })).toContainText('Index for search');
  await page.keyboard.press('Escape');
  await page.goto(`${listUrl.split('?')[0]}/settings`);
  // The indexing pipeline: the built-in is listed; it is off here, so automatic indexing is off (on demand only).
  const pipelines = page.getByRole('list', { name: 'Indexing pipelines' });
  await expect(pipelines).toContainText('Index for search');
  await expect(page.getByText('Automatic indexing is off; items are indexed on demand.')).toBeVisible();
  const included = page.getByRole('checkbox', { name: 'Include this list in search' });
  await expect(included).toBeChecked();
  await included.uncheck();
  await expect(page.getByText('Excluded from search.', { exact: true })).toBeVisible();
  const found = await (await request.get(`/v1.0/search?q=${encodeURIComponent(title)}`, { headers })).json();
  expect(found.value).toHaveLength(0);
  await included.check();
  await expect(page.getByText('Included in search. Reindexing requested.', { exact: true })).toBeVisible();
  await expect(async () => {
    const found = await (await request.get(`/v1.0/search?q=${encodeURIComponent(title)}`, { headers })).json();
    expect(found.value).toHaveLength(1);
  }).toPass({ timeout: 30_000 });
});

test('a list indexing pipeline is copied and becomes the list own active pipeline', async ({ page, request }) => {
  test.setTimeout(90_000);
  await signIn(page);
  const headers = await adminHeaders(request);
  const listUrl = await createList(page, 'Tasks', unique('Custom pipeline'));
  const workspaceId = /\/w\/([^/]+)/.exec(listUrl)![1];
  await page.goto(`${listUrl.split('?')[0]}/settings`);
  const pipelines = page.getByRole('list', { name: 'Indexing pipelines' });
  await expect(pipelines).toContainText('Active');
  await pipelines.getByRole('button', { name: 'Customize' }).first().click();
  await expect(page).toHaveURL(/settings\/workflows\?edit=/);
  const workflows = await (await request.get(`/v1.0/workspaces/${workspaceId}/workflows`, { headers })).json();
  const copy = workflows.find(
    (w: { provides?: string; builtIn?: string }) => w.provides === 'search.index' && !w.builtIn,
  );
  expect(copy?.enabled).toBeTruthy();
});
