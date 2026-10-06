import { expect, test } from '@playwright/test';
import { adminHeaders, signIn, unique } from './helpers';

for (const location of ['library', 'inbox'] as const) {
  test(`manually optimizes a ${location} file with automatic optimization off`, async ({ page, request }) => {
    test.setTimeout(90_000);
    await signIn(page);
    const headers = await adminHeaders(request);
    let ws: string;
    let list: string;
    if (location === 'inbox') {
      const home = await (await request.get('/v1.0/me/home', { headers })).json();
      ws = home.workspaceId;
      list = home.inboxListId;
    } else {
      const workspace = await request.post('/v1.0/workspaces', { headers, data: { name: unique('Manual files') } });
      ws = (await workspace.json()).id;
      const library = await request.post(`/v1.0/workspaces/${ws}/lists`, {
        headers,
        data: { name: 'Photos', templateKey: 'documents' },
      });
      list = (await library.json()).id;
    }
    const path = `/v1.0/workspaces/${ws}/lists/${list}`;
    const key = 'paperdotnet.storageoptimization.optimize';
    async function optimization() {
      const catalog = await request.get(`${path}/workflows/builtIns`, { headers });
      expect(catalog.ok()).toBeTruthy();
      return (await catalog.json()).find((workflow: { key: string }) => workflow.key === key);
    }
    const initial = await optimization();
    expect(initial).toMatchObject({ enabled: false, allowManualLaunch: true });
    expect(initial.workflowId).toBeUndefined();
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
    const image = Buffer.from(base64, 'base64');
    const upload = await request.post(`${path}/documents`, {
      headers,
      multipart: {
        file: {
          name: `${unique('manual-receipt')}.png`,
          mimeType: 'image/png',
          buffer: Buffer.concat([image, Buffer.alloc(100_000)]),
        },
      },
    });
    expect(upload.status()).toBe(201);
    const item = (await upload.json()).itemId as string;
    // Uploading alone never creates this optional workflow or starts an optimization review.
    expect((await optimization()).workflowId).toBeUndefined();
    await page.goto(
      location === 'inbox' ? `/inbox?item=${item}&tab=preview` : `/w/${ws}/l/${list}?item=${item}&tab=preview`,
    );
    await page.getByRole('dialog').getByRole('button', { name: 'Run workflow', exact: true }).click();
    const picker = page.getByRole('dialog', { name: 'Run workflow', exact: true });
    await picker.getByLabel('Workflow', { exact: true }).selectOption(`builtin:${key}`);
    await expect(
      picker.getByLabel('Workflow').getByRole('option', { name: 'Optimize document storage', exact: true }),
    ).toHaveCount(1);
    const launched = page.waitForResponse(
      (response) =>
        response.url().endsWith(`/workflows/builtIns/${key}/runs`) && response.request().method() === 'POST',
    );
    await picker.getByRole('button', { name: 'Launch workflow', exact: true }).click();
    expect((await launched).status()).toBe(200);
    await expect(picker).toBeHidden();
    expect(await optimization()).toMatchObject({ enabled: false, allowManualLaunch: true });
    let approvalId: string | undefined;
    await expect
      .poll(
        async () => {
          const pending = await (await request.get('/v1.0/me/approvals', { headers })).json();
          approvalId = pending.value.find((approval: { itemId: string }) => approval.itemId === item)?.id;
          return !!approvalId;
        },
        { timeout: 60_000 },
      )
      .toBeTruthy();
    expect((await request.get(`${path}/items/${item}/file`, { headers })).headers()['content-type']).toContain(
      'image/png',
    );
    expect(
      (
        await request.post(`/v1.0/me/approvals/${approvalId}/decision`, { headers, data: { outcome: 'rejected' } })
      ).ok(),
    ).toBeTruthy();
  });
}

for (const outcome of ['approved', 'rejected', 'stale', 'form'] as const) {
  test(`48MiB image review opens at native 100% and is ${outcome}`, async ({ page, request }) => {
    test.setTimeout(120_000);
    await signIn(page);
    const headers = await adminHeaders(request);
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
    if (outcome === 'form') {
      const builtIns = await request.get(`${path}/workflows/builtIns`, { headers });
      const workflowId = (await builtIns.json()).find(
        (workflow: { key: string }) => workflow.key === 'paperdotnet.storageoptimization.optimize',
      ).workflowId as string;
      const current = await request.get(`/v1.0/workspaces/${ws}/workflows/${workflowId}`, { headers });
      const definition = await current.json();
      const disabled = await request.put(`${path}/workflows/builtIns/paperdotnet.storageoptimization.optimize`, {
        headers: { ...headers, 'If-Match': current.headers().etag! },
        data: { enabled: false },
      });
      expect(disabled.ok()).toBeTruthy();
      definition.flow.nodes.review.inputs.inputSchema = {
        type: 'object',
        properties: { reason: { type: 'string', title: 'Review reason', minLength: 3 } },
        required: ['reason'],
      };
      const created = await request.post(`/v1.0/workspaces/${ws}/workflows`, {
        headers,
        data: {
          name: 'Image review with form',
          trigger: { type: 'document.added', list: 'Photos', data: { source: 'upload' } },
          flow: definition.flow,
        },
      });
      expect(created.status()).toBe(201);
    }
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
      name: outcome === 'approved' || outcome === 'form' ? 'Store optimized file' : 'Keep original',
    });
    await expect(action).toBeEnabled();
    if (outcome === 'form') {
      await action.click();
      await expect(dialog).toBeVisible();
      const pending = await request.get('/v1.0/me/approvals', { headers });
      expect((await pending.json()).value.some((approval: { id: string }) => approval.id === approvalId)).toBeTruthy();
      await dialog.getByLabel('Review reason', { exact: false }).fill('Text remains legible');
      const responsePromise = page.waitForResponse(
        (response) =>
          response.url().endsWith(`/approvals/${approvalId}/decision`) && response.request().method() === 'POST',
      );
      await action.click();
      const response = await responsePromise;
      expect(response.ok()).toBeTruthy();
      expect(response.request().postDataJSON()).toMatchObject({
        outcome: 'approved',
        inputs: { reason: 'Text remains legible' },
      });
    } else {
      await action.click();
    }
    await expect(dialog).not.toBeVisible();
    await expect
      .poll(async () => {
        const file = await request.get(`${path}/items/${item}/file`, { headers });
        return file.headers()['content-type'];
      })
      .toContain(outcome === 'approved' || outcome === 'form' ? 'image/webp' : 'image/png');
    if (outcome === 'approved' || outcome === 'form') {
      expect((await request.get(`${path}/items/${item}/file/versions/1`, { headers })).status()).toBe(404);
    }
  });
}
