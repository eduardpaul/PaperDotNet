import { expect, test } from '@playwright/test';
import { adminHeaders, signIn, unique } from './helpers';

// External-feed behavior and permissions are exercised by the real .NET integration tests.
// These browser fixtures exercise the generated SDK and the settings controls without a live provider.
for (const tag of ['', ' @phone']) {
  test(`multiple calendar sources have independent controls${tag}`, async ({ page, request }) => {
    await signIn(page);
    const headers = await adminHeaders(request);
    const workspace = await request.post('/v1.0/workspaces', { headers, data: { name: unique('Calendar feeds') } });
    const workspaceId = (await workspace.json()).id as string;
    const list = await request.post(`/v1.0/workspaces/${workspaceId}/lists`, {
      headers,
      data: { name: 'Combined feeds', templateKey: 'calendar' },
    });
    const listId = (await list.json()).id as string;
    const path = `/w/${workspaceId}/l/${listId}`;
    type Source = {
      id: string;
      name: string;
      paused: boolean;
      refreshing: boolean;
      '@odata.etag': string;
      lastSuccess: string;
    };
    let sources: Source[] = [];
    let sequence = 0;
    await page.route('**/v1.0/workspaces/*/lists/*/calendarSources**', async (route) => {
      const request = route.request();
      const url = new URL(request.url());
      const tail = url.pathname.split('/calendarSources')[1];
      if (request.method() === 'GET') return route.fulfill({ json: sources });
      if (request.method() === 'POST' && (!tail || tail === '/')) {
        const body = request.postDataJSON() as { name: string; url: string };
        expect(body.url).toMatch(/^https:\/\//);
        sequence++;
        const source = {
          id: `00000000-0000-4000-8000-${String(sequence).padStart(12, '0')}`,
          name: body.name,
          paused: false,
          refreshing: false,
          '@odata.etag': '"1"',
          lastSuccess: '2026-10-02T10:00:00Z',
        };
        sources = [...sources, source];
        return route.fulfill({ status: 201, json: source });
      }
      const id = tail.split('/')[1];
      const source = sources.find((s) => s.id === id)!;
      if (request.method() === 'PUT') {
        expect(request.headers()['if-match']).toBe(source['@odata.etag']);
        Object.assign(source, request.postDataJSON());
        return route.fulfill({ json: source });
      }
      if (request.method() === 'DELETE') {
        expect(request.headers()['if-match']).toBe(source['@odata.etag']);
        sources = sources.filter((s) => s.id !== id);
        return route.fulfill({ status: 204 });
      }
      return route.fulfill({ status: 202, json: { operationId: '00000000-0000-4000-8000-000000000010' } });
    });
    await page.goto(path + '/settings/calendar-sources');
    await expect(page.getByRole('heading', { name: 'Calendar sources', exact: true })).toBeVisible();
    for (const name of ['Work feed', 'Family feed']) {
      await page.getByLabel('Name', { exact: true }).fill(name);
      await page
        .getByLabel('iCalendar URL', { exact: true })
        .fill(`https://calendar.example/${name.split(' ')[0]}/secret.ics`);
      await page.getByRole('button', { name: 'Add source', exact: true }).click();
      await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    }
    await expect(page.getByLabel('iCalendar URL', { exact: true })).toHaveValue('');
    const work = page.getByRole('listitem').filter({ has: page.getByRole('heading', { name: 'Work feed' }) });
    const family = page.getByRole('listitem').filter({ has: page.getByRole('heading', { name: 'Family feed' }) });
    await work.getByRole('button', { name: 'Pause', exact: true }).click();
    await expect(work.getByRole('button', { name: 'Resume', exact: true })).toBeVisible();
    await expect(family.getByRole('button', { name: 'Pause', exact: true })).toBeVisible();
    await family.getByRole('button', { name: 'Refresh now' }).click();
    await expect(page.getByText('Refresh queued.', { exact: true })).toBeVisible();
    await work.getByRole('button', { name: 'Remove source' }).click();
    await expect(page.getByRole('heading', { name: 'Work feed', exact: true })).toBeHidden();
    await expect(page.getByRole('heading', { name: 'Family feed', exact: true })).toBeVisible();
  });
}
