import { expect, test } from '@playwright/test';
import { adminHeaders, createList, signIn, unique } from './helpers';

test('selected photos become one reviewed searchable PDF in the chosen order', async ({ page, request }) => {
  test.setTimeout(120_000);
  await signIn(page);
  const headers = await adminHeaders(request);
  expect(
    (await request.post('/v1.0/extensions/paperdotnet.storageoptimization/enable', { headers })).ok(),
  ).toBeTruthy();
  const libraryUrl = await createList(page, 'Documents', unique('Photo pages'));
  const ws = /\/w\/([^/]+)/.exec(libraryUrl)![1];
  const list = /\/l\/([^/]+)/.exec(libraryUrl)![1];
  const path = `/v1.0/workspaces/${ws}/lists/${list}`;
  const ids: string[] = [];
  for (let index = 0; index < 2; index++) {
    const base64 = await page.evaluate((value) => {
      const canvas = document.createElement('canvas');
      canvas.width = 900;
      canvas.height = 1200;
      const context = canvas.getContext('2d')!;
      context.fillStyle = 'white';
      context.fillRect(0, 0, canvas.width, canvas.height);
      context.fillStyle = 'black';
      context.font = '55px sans-serif';
      context.fillText(`DOCUMENT PAGE ${value + 1}`, 50, 150);
      context.fillText(`RECEIPT ${4711 + value}`, 50, 250);
      return canvas.toDataURL('image/png').split(',')[1]!;
    }, index);
    const upload = await request.post(`${path}/documents`, {
      headers,
      multipart: {
        file: { name: `photo-${index + 1}.png`, mimeType: 'image/png', buffer: Buffer.from(base64, 'base64') },
      },
    });
    expect(upload.status()).toBe(201);
    ids.push((await upload.json()).itemId);
  }
  await page.reload();
  await page.getByRole('checkbox', { name: 'Select all', exact: true }).check();
  await page.getByRole('button', { name: 'Run workflow', exact: true }).click();
  const picker = page.getByRole('dialog', { name: 'Run workflow' });
  await picker
    .getByLabel('Workflow', { exact: true })
    .selectOption('builtin:paperdotnet.storageoptimization.photoToDocument');
  await expect(picker.getByText('Run once for all 2 selected items.')).toBeVisible();
  const primary = picker.getByLabel('Primary item', { exact: true });
  const order = await primary
    .locator('option')
    .evaluateAll((options) => options.map((option) => (option as HTMLOptionElement).value));
  await primary.selectOption(ids[1]!);
  await picker.getByRole('button', { name: 'Move page 1 down', exact: true }).click();
  await expect(primary).toHaveValue(ids[1]!);
  const launched = page.waitForResponse(
    (response) =>
      response.url().endsWith('/paperdotnet.storageoptimization.photoToDocument/runs') &&
      response.request().method() === 'POST',
  );
  await picker.getByRole('button', { name: 'Launch workflow', exact: true }).click();
  const launch = await launched;
  expect(launch.status()).toBe(200);
  expect(launch.request().postDataJSON()).toMatchObject({ itemIds: [order[1], order[0]], primaryItemId: ids[1] });
  const runs = await launch.json();
  expect(runs).toHaveLength(1);
  let approval: string | undefined;
  await expect
    .poll(
      async () => {
        const approvals = await (await request.get('/v1.0/me/approvals', { headers })).json();
        approval = approvals.value.find((entry: { runId: string }) => entry.runId === runs[0].id)?.id;
        return !!approval;
      },
      { timeout: 60_000 },
    )
    .toBeTruthy();
  for (const id of ids)
    expect((await request.get(`${path}/items/${id}/file`, { headers })).headers()['content-type']).toContain(
      'image/png',
    );
  await page.goto('/approvals');
  await page
    .getByRole('listitem')
    .filter({ hasText: 'Review combined PDF for photo-2' })
    .getByRole('button', { name: 'Review file' })
    .click();
  const review = page.getByRole('dialog', { name: 'Review combined PDF for photo-2' });
  await expect(review.getByRole('img', { name: 'PDF page 1', exact: true })).toBeVisible();
  await review.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(review.getByRole('img', { name: 'PDF page 2', exact: true })).toBeVisible();
  await review.getByLabel('Review zoom').selectOption('150');
  const approved = page.waitForResponse(
    (response) => response.url().endsWith('/decision') && response.request().method() === 'POST',
  );
  await review.getByRole('button', { name: 'Replace photos with PDF', exact: true }).click();
  expect((await approved).status()).toBe(200);
  await expect
    .poll(
      async () => (await (await request.get(`${path}/items/${ids[1]}/file`, { headers })).headers())['content-type'],
    )
    .toContain('application/pdf');
  await expect.poll(async () => (await request.get(`${path}/items/${ids[0]}`, { headers })).status()).toBe(404);
  const versions = await (await request.get(`${path}/items/${ids[1]}/file/versions`, { headers })).json();
  expect(versions.value).toHaveLength(2);
  expect(versions.value[0]).toMatchObject({ pageCount: 2, source: 'composition' });
  await page.goto(libraryUrl);
  await expect(page.getByRole('row', { name: /photo-2/ })).toBeVisible();
  await expect(page.getByRole('row', { name: /photo-1/ })).toHaveCount(0);
});
