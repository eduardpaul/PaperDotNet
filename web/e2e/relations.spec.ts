import { expect, test } from '@playwright/test';
import { adminHeaders, createList, signIn, unique } from './helpers';

test('items of different types have symmetric links that survive moving between workspaces', async ({
  page,
  request,
}) => {
  await signIn(page);
  const sourceUrl = await createList(page, 'Tasks', unique('Source tasks'));
  const source = new URL(sourceUrl).pathname.split('/');
  const targetName = unique('Destination tasks');
  const targetUrl = await createList(page, 'Tasks', targetName);
  const target = new URL(targetUrl).pathname.split('/');
  const headers = await adminHeaders(request);
  const noteList = await request.post(`/v1.0/workspaces/${target[2]}/lists`, {
    headers,
    data: { name: unique('Related notes'), templateKey: 'notes' },
  });
  expect(noteList.ok()).toBeTruthy();
  const noteListId = (await noteList.json()).id;
  const taskTitle = unique('Task to move');
  const noteTitle = unique('Supporting note');
  const task = await request.post(`/v1.0/workspaces/${source[2]}/lists/${source[4]}/items`, {
    headers,
    data: { fields: { title: taskTitle } },
  });
  expect(task.ok()).toBeTruthy();
  const taskId = (await task.json()).id;
  const note = await request.post(`/v1.0/workspaces/${target[2]}/lists/${noteListId}/items`, {
    headers,
    data: { fields: { title: noteTitle } },
  });
  expect(note.ok()).toBeTruthy();
  const noteId = (await note.json()).id;

  await page.goto(`/i/${taskId}`);
  const panel = page.getByRole('dialog');
  await panel.getByRole('tab', { name: 'Related', exact: true }).click();
  await panel.getByLabel('Link another item').fill(noteTitle);
  await panel
    .getByRole('list', { name: 'Items to link' })
    .getByRole('button', { name: new RegExp(noteTitle) })
    .click();
  await expect(
    panel.getByRole('list', { name: 'Related items' }).getByRole('link', { name: new RegExp(noteTitle) }),
  ).toBeVisible();
  await panel.getByRole('link', { name: new RegExp(noteTitle) }).click();
  await panel.getByRole('tab', { name: 'Related', exact: true }).click();
  await expect(panel.getByRole('list', { name: 'Related items' })).toContainText(taskTitle);

  await page.goto(`/i/${taskId}`);
  await panel.getByRole('button', { name: 'Item actions' }).click();
  await page.getByRole('menuitem', { name: 'Move to a list…' }).click();
  const destination = page.getByLabel('List', { exact: true });
  await destination.selectOption((await destination.locator('option', { hasText: targetName }).getAttribute('value'))!);
  await page.getByRole('button', { name: 'Move', exact: true }).click();
  await expect(page.getByText(`Moved to ${targetName}.`)).toBeVisible();

  // The same permanent URL resolves the same id at its new location.
  await page.goto(`/i/${taskId}`);
  await expect(page).toHaveURL(new RegExp(`/w/${target[2]}/l/${target[4]}\\?item=${taskId}`));
  await panel.getByRole('tab', { name: 'Related', exact: true }).click();
  await expect(panel.getByRole('list', { name: 'Related items' })).toContainText(noteTitle);
  await page.goto(`/i/${noteId}`);
  await panel.getByRole('tab', { name: 'Related', exact: true }).click();
  await expect(panel.getByRole('list', { name: 'Related items' })).toContainText(targetName);
  await panel.getByRole('button', { name: `Unlink ${taskTitle}`, exact: true }).click();
  await expect(panel.getByText('No related items you can access.')).toBeVisible();
  await page.goto(`/i/${taskId}`);
  await panel.getByRole('tab', { name: 'Related', exact: true }).click();
  await expect(panel.getByText('No related items you can access.')).toBeVisible();
});
