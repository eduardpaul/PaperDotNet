import { expect, test } from '@playwright/test';
import { adminHeaders, signIn, unique } from './helpers';

for (const outcome of ['approved', 'rejected', 'stale'] as const) {
  test(`48MiB image review opens at native 100% and is ${outcome}`, async ({ page, request }) => {
    test.setTimeout(120_000);
    await signIn(page);
    const headers = await adminHeaders(request);
    expect(
      (await request.post('/v1.0/extensions/paperdotnet.storageoptimization/enable', { headers })).ok(),
    ).toBeTruthy();
    const workspace = await request.post('/v1.0/workspaces', { headers, data: { name: unique('Optimization') } });
    const ws = (await workspace.json()).id as string;
    const library = await request.post(`/v1.0/workspaces/${ws}/lists`, {
      headers,
      data: { name: 'Photos', templateKey: 'documents' },
    });
    const list = (await library.json()).id as string;
    const path = `/v1.0/workspaces/${ws}/lists/${list}`;
    const enabled = await request.put(`${path}/workflows/builtIns/paperdotnet.storageoptimization.optimize`, {
      headers,
      data: { enabled: true },
    });
    expect(enabled.ok()).toBeTruthy();
    const base64 = await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = 1600;
      canvas.height = 2100;
      const context = canvas.getContext('2d')!;
      context.fillStyle = 'white';
      context.fillRect(0, 0, canvas.width, canvas.height);
      context.fillStyle = 'black';
      context.font = '80px sans-serif';
      for (let line = 0; line < 12; line++) context.fillText('RECEIPT TOTAL 123.45', 60, 130 + line * 130);
      return canvas.toDataURL('image/png').split(',')[1]!;
    });
    const source = Buffer.alloc(48 * 1024 * 1024);
    Buffer.from(base64, 'base64').copy(source);
    const fileName = unique('receipt');
    const upload = await request.post(`${path}/documents`, {
      headers,
      multipart: { file: { name: `${fileName}.png`, mimeType: 'image/png', buffer: source } },
    });
    expect(upload.status()).toBe(201);
    const item = (await upload.json()).itemId as string;
    let approvalId: string | undefined;
    await expect
      .poll(
        async () => {
          const response = await request.get('/v1.0/me/approvals', { headers });
          const approval = (await response.json()).value.find((a: { itemId: string }) => a.itemId === item);
          approvalId = approval?.id;
          return !!approvalId;
        },
        { timeout: 60_000 },
      )
      .toBeTruthy();
    const sourceUrl = `**/v1.0/me/approvals/${approvalId}/review/content/source`;
    if (outcome === 'approved') {
      await page.route(sourceUrl, (route) => route.fulfill({ status: 503 }));
    }
    await page.goto('/approvals');
    const row = page.getByRole('listitem').filter({ hasText: `Review smaller file for ${fileName}` });
    await row.getByRole('button', { name: 'Review file' }).click();
    const dialog = page.getByRole('dialog');
    if (outcome === 'approved') {
      await expect(dialog.getByText('Review images are unavailable.', { exact: false })).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Store optimized file' })).toBeDisabled();
      await expect(dialog.getByRole('button', { name: 'Keep original' })).toBeDisabled();
      await page.unroute(sourceUrl);
      await dialog.getByRole('button', { name: 'Refresh review' }).click();
    }
    const original = dialog.getByAltText('Original document');
    const candidate = dialog.getByAltText('Optimized document');
    await expect(original).toBeVisible();
    await expect(candidate).toBeVisible();
    await expect.poll(() => original.evaluate((element) => element.getBoundingClientRect().width)).toBe(1600);
    await expect
      .poll(() =>
        candidate.evaluate((element) => {
          const image = element as HTMLImageElement;
          return image.getBoundingClientRect().width === image.naturalWidth;
        }),
      )
      .toBeTruthy();
    await dialog.getByRole('button', { name: 'Fit original' }).click();
    await expect.poll(() => original.evaluate((element) => element.getBoundingClientRect().width)).toBeLessThan(1600);
    await dialog.getByRole('button', { name: '100% original' }).click();
    await expect.poll(() => original.evaluate((element) => element.getBoundingClientRect().width)).toBe(1600);
    await dialog.getByLabel('Link pan positions').check();
    const pane = dialog.getByLabel('Pan original');
    await pane.evaluate((element) => element.scrollTo(200, 300));
    await expect.poll(() => pane.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    if (outcome === 'stale') {
      const replaced = await request.put(`${path}/items/${item}/file`, {
        headers,
        multipart: { file: { name: 'new.png', mimeType: 'image/png', buffer: Buffer.from(base64, 'base64') } },
      });
      expect(replaced.ok()).toBeTruthy();
      expect(
        (
          await request.post(`/v1.0/me/approvals/${approvalId}/decision`, { headers, data: { outcome: 'approved' } })
        ).status(),
      ).toBe(409);
      await dialog.getByRole('button', { name: 'Refresh review' }).click();
      await expect(dialog.getByRole('button', { name: 'Store optimized file' })).toBeDisabled();
      return;
    }
    const action = dialog.getByRole('button', {
      name: outcome === 'approved' ? 'Store optimized file' : 'Keep original',
    });
    await expect(action).toBeEnabled();
    await action.click();
    await expect(dialog).not.toBeVisible();
    await expect
      .poll(async () => {
        const file = await request.get(`${path}/items/${item}/file`, { headers });
        return file.headers()['content-type'];
      })
      .toContain(outcome === 'approved' ? 'image/webp' : 'image/png');
    if (outcome === 'approved') {
      expect((await request.get(`${path}/items/${item}/file/versions/1`, { headers })).status()).toBe(404);
    }
  });
}
