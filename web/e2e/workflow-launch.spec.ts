import { expect, test } from '@playwright/test';
import { adminHeaders, createList, createUser, signIn, unique } from './helpers';

test('launch forms, workspace event filters, and approval forms', async ({ page, request }) => {
  test.setTimeout(90_000);
  const headers = await adminHeaders(request);
  await signIn(page);
  const listName = unique('Tasks');
  const listUrl = await createList(page, 'Tasks', listName);
  const workspaceUrl = listUrl.replace(/\/l\/.*$/, '');
  const workspaceId = /\/w\/([^/]+)/.exec(workspaceUrl)![1];
  const listId = /\/l\/([^/]+)/.exec(listUrl)![1];
  const root = `/v1.0/workspaces/${workspaceId}`;
  const flow = {
    start: 'inspect',
    nodes: {
      inspect: {
        activity: 'script',
        inputs: {
          code: 'return { label: input.label, mode: input.mode, urgent: input.urgent, source: context.trigger };',
        },
      },
    },
  };
  const inputSchema = {
    type: 'object',
    properties: {
      label: { type: 'string', title: 'Label', minLength: 1 },
      mode: { type: 'string', title: 'Mode', enum: ['week', 'month'], default: 'week' },
      urgent: { type: 'boolean', title: 'Urgent', default: false },
    },
    required: ['label'],
  };
  const workspaceFlow = await request.post(`${root}/workflows`, {
    headers,
    data: {
      name: 'Workspace report',
      scope: 'workspace',
      trigger: { type: 'manual' },
      inputSchema,
      flow,
    },
  });
  expect(workspaceFlow.status()).toBe(201);
  const listFlow = await request.post(`${root}/workflows`, {
    headers,
    data: {
      name: 'Item report',
      scope: 'list',
      trigger: { type: 'manual', list: listName },
      inputSchema,
      flow,
    },
  });
  expect(listFlow.status()).toBe(201);
  const item = await request.post(`${root}/lists/${listId}/items`, {
    headers,
    data: { fields: { title: 'Selected item' } },
  });
  expect(item.status()).toBe(201);

  await page.goto(workspaceUrl);
  await page.getByRole('button', { name: 'Run workflow' }).click();
  let dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await expect(dialog.getByLabel('Workflow')).toHaveValue((await workspaceFlow.json()).id);
  await expect(dialog.getByLabel('Workflow').getByRole('option')).toHaveCount(1);
  await dialog.getByLabel('Label', { exact: false }).fill('Monthly report');
  await dialog.getByLabel('Mode', { exact: false }).selectOption('month');
  await dialog.getByLabel('Urgent', { exact: false }).check();
  const launched = page.waitForResponse(
    (response) => response.url().endsWith('/runs') && response.request().method() === 'POST',
  );
  await dialog.getByRole('button', { name: 'Launch workflow' }).click();
  const runs = await (await launched).json();
  expect(runs[0].executionContext.input).toEqual({ label: 'Monthly report', mode: 'month', urgent: true });
  expect(runs[0].executionContext.itemId).toBeNull();
  await expect(dialog).toBeHidden();

  await page.goto(listUrl);
  await page.getByRole('row').filter({ hasText: 'Selected item' }).getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Run workflow' }).click();
  dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await expect(dialog.getByLabel('Workflow').getByRole('option')).toHaveCount(1);
  await expect(dialog.getByLabel('Workflow')).toHaveValue((await listFlow.json()).id);
  await dialog.getByLabel('Label', { exact: false }).fill('Item report');
  const itemLaunch = page.waitForResponse(
    (response) => response.url().endsWith('/runs') && response.request().method() === 'POST',
  );
  await dialog.getByRole('button', { name: 'Launch workflow' }).click();
  const itemRuns = await (await itemLaunch).json();
  expect(itemRuns[0].itemId).toBe((await item.json()).id);
  expect(itemRuns[0].executionContext.input).toEqual({ label: 'Item report', mode: 'week', urgent: false });
  await expect(dialog).toBeHidden();

  const groupName = unique('Workflow tags');
  const group = await request.post('/v1.0/termStore/groups', { headers, data: { name: groupName } });
  expect(group.status()).toBe(201);
  const set = await request.post('/v1.0/termStore/sets', {
    headers,
    data: { groupId: (await group.json()).id, name: 'Tags' },
  });
  expect(set.status()).toBe(201);
  const term = await request.post(`/v1.0/termStore/sets/${(await set.json()).id}/terms`, {
    headers,
    data: { name: 'Receipt' },
  });
  expect(term.status()).toBe(201);
  await page.goto(`${workspaceUrl}/settings/workflows`);
  await page.getByRole('button', { name: /^Workspace report/ }).click();
  const editor = page.getByRole('dialog');
  await editor.getByLabel('Trigger', { exact: true }).selectOption('itemUpdated');
  await expect(editor.getByLabel('List', { exact: true })).toHaveValue('');
  await editor.getByLabel('Content type', { exact: true }).fill('Paper');
  await editor
    .getByLabel('Trigger parameters', { exact: true })
    .fill(JSON.stringify({ when: { target: 'tags', operator: 'added', term: `${groupName}/Tags/Receipt` } }));
  const saved = page.waitForResponse(
    (response) => response.url().includes(`${root}/workflows/`) && response.request().method() === 'PUT',
  );
  await editor.getByRole('button', { name: 'Save', exact: true }).click();
  const savedResponse = await saved;
  expect(savedResponse.status()).toBe(200);
  expect(savedResponse.request().postDataJSON()).toMatchObject({
    scope: 'workspace',
    trigger: {
      type: 'itemUpdated',
      contentType: 'Paper',
      parameters: { when: { target: 'tags', operator: 'added', term: `${groupName}/Tags/Receipt` } },
    },
  });
  await expect(editor).toBeHidden();

  const approvalWorkflow = await request.post(`${root}/workflows`, {
    headers,
    data: {
      name: 'Collect approval information',
      scope: 'workspace',
      trigger: { type: 'manual' },
      flow: {
        start: 'review',
        nodes: {
          review: {
            activity: 'approval',
            inputs: { title: 'Collect approval information', assignees: ['admin'], inputSchema },
            next: { approved: 'finish', rejected: 'finish' },
          },
          finish: { activity: 'script', inputs: { code: 'return steps.review.input;' } },
        },
      },
    },
  });
  expect(approvalWorkflow.status()).toBe(201);
  const approvalLaunch = await request.post(`${root}/workflows/${(await approvalWorkflow.json()).id}/runs`, {
    headers,
    data: {},
  });
  expect(approvalLaunch.status()).toBe(200);
  const approvalRunId = (await approvalLaunch.json())[0].id;
  await expect
    .poll(async () => {
      const pending = await request.get('/v1.0/me/approvals', { headers });
      return (await pending.json()).value.some((a: { runId: string }) => a.runId === approvalRunId);
    })
    .toBe(true);
  await page.goto('/approvals');
  const approvalRow = page.getByRole('listitem').filter({ hasText: 'Collect approval information' });
  await approvalRow.getByRole('button', { name: 'Review approval', exact: true }).click();
  await approvalRow.getByLabel('Label', { exact: false }).fill('Decision information');
  await approvalRow.getByLabel('Mode', { exact: false }).selectOption('month');
  await approvalRow.getByLabel('Urgent', { exact: false }).check();
  const decided = page.waitForResponse(
    (response) => response.url().endsWith('/decision') && response.request().method() === 'POST',
  );
  await approvalRow.getByRole('button', { name: 'Reject', exact: true }).click();
  const decisionResponse = await decided;
  expect(decisionResponse.status()).toBe(200);
  expect((await decisionResponse.json()).inputs).toEqual({
    label: 'Decision information',
    mode: 'month',
    urgent: true,
  });
  expect(decisionResponse.request().postDataJSON().outcome).toBe('rejected');
  await expect
    .poll(async () => (await (await request.get(`${root}/workflows/runs/${approvalRunId}`, { headers })).json()).status)
    .toBe('completed');
  const completedRun = await (await request.get(`${root}/workflows/runs/${approvalRunId}`, { headers })).json();
  expect(completedRun.outputs.finish.result).toEqual({ label: 'Decision information', mode: 'month', urgent: true });
  await page.getByRole('tab', { name: 'Rejected', exact: true }).click();
  await approvalRow.getByRole('button', { name: 'View information', exact: true }).click();
  await expect(approvalRow.getByLabel('Label', { exact: false })).toHaveValue('Decision information');
  await expect(approvalRow.getByLabel('Label', { exact: false })).toBeDisabled();
});

test('domain selectors collect scoped tags, keywords and relationship targets', async ({ page, request }) => {
  test.setTimeout(90_000);
  const headers = await adminHeaders(request);
  await signIn(page);
  const listUrl = await createList(page, 'Tasks', unique('Domain tasks'));
  const workspaceUrl = listUrl.replace(/\/l\/.*$/, '');
  const workspaceId = /\/w\/([^/]+)/.exec(workspaceUrl)![1];
  const listId = /\/l\/([^/]+)/.exec(listUrl)![1];
  async function create(url: string, data: object): Promise<string> {
    const response = await request.post(url, { headers, data });
    expect(response.ok()).toBe(true);
    return (await response.json()).id;
  }
  const title = unique('Relationship target');
  const target = await create(`/v1.0/workspaces/${workspaceId}/lists/${listId}/items`, { fields: { title } });
  const relationshipType = await create('/v1.0/relationshipTypes', { name: unique('Depends on'), directed: true });
  const group = await create('/v1.0/termStore/groups', { name: unique('Form terms') });
  const set = await create('/v1.0/termStore/sets', { groupId: group, name: 'Tags' });
  const tag = await create(`/v1.0/termStore/sets/${set}/terms`, { name: 'Allowed tag' });
  const keywordName = unique('Keyword');
  const keyword = await create('/v1.0/termStore/keywords', { name: keywordName });
  const inputSchema = {
    type: 'object',
    properties: {
      target: { type: 'string', title: 'Dependency', 'x-paperdotnet': { kind: 'relationship', relationshipType } },
      tags: {
        type: 'array',
        title: 'Scoped tags',
        items: { type: 'string' },
        'x-paperdotnet': { kind: 'terms', groupId: group, termSetId: set, termIds: [tag] },
      },
      keyword: { type: 'string', title: 'Keyword', 'x-paperdotnet': { kind: 'keywords', termIds: [keyword] } },
    },
    required: ['target', 'tags', 'keyword'],
  };
  const workflow = await create(`/v1.0/workspaces/${workspaceId}/workflows`, {
    name: unique('Domain workflow'),
    scope: 'workspace',
    trigger: { type: 'manual' },
    inputSchema,
    steps: [{ type: 'approval', name: 'Domain review', assignees: ['admin'], inputSchema }],
  });
  await page.goto(workspaceUrl);
  await page.getByRole('button', { name: 'Run workflow' }).click();
  const dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await dialog.getByLabel('Workflow').selectOption(workflow);
  async function fill(scope: typeof dialog) {
    await scope.getByRole('combobox', { name: 'Dependency' }).click();
    await page.getByPlaceholder('Search…', { exact: true }).fill(title);
    await page.getByRole('option', { name: title, exact: true }).click();
    await scope.getByRole('combobox', { name: 'Scoped tags' }).click();
    await page.getByRole('option', { name: 'Allowed tag', exact: true }).click();
    await page.keyboard.press('Escape');
    await scope.getByRole('combobox', { name: 'Keyword', exact: false }).click();
    await page.getByRole('option', { name: keywordName, exact: true }).click();
  }
  await fill(dialog);
  const launched = page.waitForResponse(
    (response) => response.url().endsWith('/runs') && response.request().method() === 'POST',
  );
  await dialog.getByRole('button', { name: 'Launch workflow' }).click();
  const run = (await (await launched).json())[0];
  expect(run.executionContext.input).toEqual({ target, tags: [tag], keyword });
  await expect
    .poll(async () => {
      const response = await request.get('/v1.0/me/approvals', { headers });
      return (await response.json()).value.some((approval: { runId: string }) => approval.runId === run.id);
    })
    .toBe(true);
  await page.goto('/approvals');
  const row = page.getByRole('listitem').filter({ hasText: 'Domain review' });
  await row.getByRole('button', { name: 'Review approval' }).click();
  await fill(row);
  const decided = page.waitForResponse(
    (response) => response.url().endsWith('/decision') && response.request().method() === 'POST',
  );
  await row.getByRole('button', { name: 'Approve', exact: true }).click();
  expect((await (await decided).json()).inputs).toEqual({ target, tags: [tag], keyword });
});

test('people selectors offer the members of a group and name the selected people', async ({ page, request }) => {
  test.setTimeout(90_000);
  const headers = await adminHeaders(request);
  await signIn(page);
  const listUrl = await createList(page, 'Tasks', unique('People tasks'));
  const workspaceUrl = listUrl.replace(/\/l\/.*$/, '');
  const workspaceId = /\/w\/([^/]+)/.exec(workspaceUrl)![1];
  async function create(url: string, data: object): Promise<string> {
    const response = await request.post(url, { headers, data });
    expect(response.ok()).toBe(true);
    return (await response.json()).id;
  }
  const memberName = unique('Nested member');
  const outsiderName = unique('Outsider');
  const member = await createUser(request, unique('member').replace(' ', '-'), memberName);
  const outsider = await createUser(request, unique('outsider').replace(' ', '-'), outsiderName);
  const reviewers = await create('/v1.0/groups', { name: unique('Reviewers') });
  const juniors = await create('/v1.0/groups', { name: unique('Juniors') });
  expect((await request.post(`/v1.0/groups/${juniors}/members`, { headers, data: { userId: member } })).ok()).toBe(
    true,
  );
  expect((await request.post(`/v1.0/groups/${reviewers}/groups`, { headers, data: { groupId: juniors } })).ok()).toBe(
    true,
  );
  const robotName = unique('Robot');
  const application = await request.post('/v1.0/applications', {
    headers,
    data: {
      displayName: robotName,
      clientType: 'confidential',
      grantTypes: ['client_credentials'],
      scopes: ['workspace.read'],
    },
  });
  expect(application.ok()).toBe(true);
  const robot = (await application.json()).application.serviceUserId;
  expect((await request.post(`/v1.0/groups/${reviewers}/members`, { headers, data: { userId: robot } })).ok()).toBe(
    true,
  );
  const inputSchema = {
    type: 'object',
    properties: {
      reviewers: {
        type: 'array',
        title: 'Reviewers',
        items: { type: 'string' },
        'x-paperdotnet': { kind: 'people', memberOf: reviewers, groups: false },
      },
      owner: { type: 'string', title: 'Owner', default: outsider, 'x-paperdotnet': { kind: 'people' } },
    },
    required: ['reviewers'],
  };
  const workflow = await create(`/v1.0/workspaces/${workspaceId}/workflows`, {
    name: unique('People workflow'),
    scope: 'workspace',
    trigger: { type: 'manual' },
    inputSchema,
    steps: [{ type: 'approval', name: 'People review', assignees: ['{input:reviewers}'] }],
  });
  await page.goto(workspaceUrl);
  await page.getByRole('button', { name: 'Run workflow' }).click();
  const dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await dialog.getByLabel('Workflow').selectOption(workflow);
  // The default shows its name, not its id.
  await expect(dialog.getByRole('combobox', { name: 'Owner' })).toContainText(outsiderName);
  await expect(dialog.getByRole('combobox', { name: 'Owner' })).not.toContainText(outsider);
  // Only the people in the group (groups inside it included), never service accounts.
  await dialog.getByRole('combobox', { name: 'Reviewers' }).click();
  await expect(page.getByRole('option', { name: memberName })).toBeVisible();
  await expect(page.getByRole('option', { name: outsiderName })).toHaveCount(0);
  await expect(page.getByRole('option', { name: robotName })).toHaveCount(0);
  await page.getByRole('option', { name: memberName }).click();
  await page.keyboard.press('Escape');
  const launched = page.waitForResponse(
    (response) => response.url().endsWith('/runs') && response.request().method() === 'POST',
  );
  await dialog.getByRole('button', { name: 'Launch workflow' }).click();
  const run = (await (await launched).json())[0];
  expect(run.executionContext.input).toEqual({ reviewers: [member], owner: outsider });
  // The selected reviewer gets the approval.
  await expect
    .poll(async () => {
      const response = await request.get(`/v1.0/workspaces/${workspaceId}/workflows/runs/${run.id}`, { headers });
      return (await response.json()).status;
    })
    .toBe('waiting');
});
