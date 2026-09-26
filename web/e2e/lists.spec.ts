import { expect, test, type Page } from '@playwright/test';
import { createList, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

async function newTask(page: Page, title: string, priority?: string) {
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(title);
  if (priority) await panel.getByLabel('Priority').selectOption(priority);
  await panel.getByRole('button', { name: 'Create' }).click();
  await expect(panel.getByRole('tab', { name: 'Details' })).toBeVisible();
  await panel.getByRole('button', { name: 'Close' }).first().click();
  await expect(page.getByRole('row', { name: new RegExp(title) })).toBeVisible();
}

test('items are created, edited and shown in the list with their field values', async ({ page }) => {
  await createList(page, 'Tasks', 'Sprint');
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill('Write the release notes');
  await panel.getByLabel('Priority').selectOption('high');
  await panel.getByLabel('Due date').fill('2030-05-17');
  await panel.getByRole('button', { name: 'Create' }).click();

  // The panel now shows the saved item (its URL has the item id).
  await expect(page).toHaveURL(/item=[0-9a-f-]{36}/);
  await panel.getByLabel('Status').selectOption('inProgress');
  await expect(panel.getByText('Unsaved changes')).toBeVisible();
  await panel.getByRole('button', { name: 'Save' }).click();
  await expect(panel.getByText('Unsaved changes')).toBeHidden();
  await page.keyboard.press('Escape');

  const row = page.getByRole('row', { name: /Write the release notes/ });
  await expect(row).toContainText('In progress');
  await expect(row).toContainText('High');
  await expect(row).toContainText('2030-05-17');

  // Reloading keeps the list; the item opens again from its URL.
  await row.click();
  await page.reload();
  await expect(page.getByRole('dialog').getByLabel('Title')).toHaveValue('Write the release notes');
});

test('required fields and server validation are shown next to the field', async ({ page }) => {
  await createList(page, 'Tasks', 'Validation');
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill('Too far');
  await panel.getByLabel('% complete').fill('150');
  // The browser's own check stops out-of-range numbers first; the server's message appears when it gets through.
  await panel.getByLabel('% complete').evaluate((input: HTMLInputElement) => input.removeAttribute('max'));
  await panel.getByRole('button', { name: 'Create' }).click();
  await expect(panel.getByText(/100/)).toBeVisible();
  await expect(panel.getByLabel('% complete')).toHaveAttribute('aria-invalid', 'true');
});

test('a change by someone else is not overwritten silently', async ({ page, context }) => {
  const url = await createList(page, 'Tasks', 'Conflicts');
  await newTask(page, 'Shared task');
  await page.getByRole('row', { name: /Shared task/ }).click();
  const itemUrl = page.url();

  const other = await context.newPage();
  await other.goto(itemUrl);
  await other.getByRole('dialog').getByLabel('Priority').selectOption('low');
  await other.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect(other.getByRole('dialog').getByText('Unsaved changes')).toBeHidden();

  const panel = page.getByRole('dialog');
  await panel.getByLabel('Priority').selectOption('high');
  await panel.getByRole('button', { name: 'Save' }).click();
  await expect(panel.getByText('Someone else changed this item')).toBeVisible();
  await panel.getByRole('button', { name: 'Reload their version' }).click();
  await expect(panel.getByLabel('Priority')).toHaveValue('low');
  expect(url).toContain('/l/');
});

test('the board groups by status and cards move between columns', async ({ page }) => {
  await createList(page, 'Tasks', 'Board');
  await newTask(page, 'Card one');
  await page.getByRole('tab', { name: 'Board' }).click();

  const notStarted = page.getByRole('region', { name: 'Not started' });
  const inProgress = page.getByRole('region', { name: 'In progress' });
  await expect(notStarted.getByText('Card one')).toBeVisible();
  await notStarted.getByRole('button', { name: 'Move to' }).click();
  await page.getByRole('menuitem', { name: 'In progress' }).click();
  await expect(inProgress.getByText('Card one')).toBeVisible();
  await expect(notStarted.getByText('Card one')).toBeHidden();
});

test('selected items are edited together, deleted and restored from the recycle bin', async ({ page }) => {
  await createList(page, 'Tasks', 'Bulk');
  const first = unique('Alpha');
  const second = unique('Beta');
  await newTask(page, first, 'low');
  await newTask(page, second, 'low');

  await page
    .getByRole('row', { name: new RegExp(first) })
    .getByRole('checkbox')
    .check();
  await page
    .getByRole('row', { name: new RegExp(second) })
    .getByRole('checkbox')
    .check();
  await expect(page.getByText('2 selected')).toBeVisible();
  await page.getByRole('button', { name: 'Edit field' }).click();
  await page.getByLabel('Field').selectOption({ label: 'Priority' });
  await page.getByLabel('Value').selectOption('high');
  await page.getByRole('button', { name: 'Apply' }).click();
  await expect(page.getByRole('row', { name: new RegExp(first) })).toContainText('High');
  await expect(page.getByRole('row', { name: new RegExp(second) })).toContainText('High');

  await page
    .getByRole('row', { name: new RegExp(first) })
    .getByRole('checkbox')
    .check();
  await page.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('row', { name: new RegExp(first) })).toBeHidden();

  await page.getByRole('link', { name: 'Recycle bin' }).click();
  await page.getByRole('listitem').filter({ hasText: first }).getByRole('button', { name: 'Restore' }).click();
  await expect(page.getByText('The recycle bin is empty')).toBeVisible();
  await page.getByRole('link', { name: 'Bulk' }).first().click();
  await expect(page.getByRole('row', { name: new RegExp(first) })).toBeVisible();

  // Deleting from the item panel, then for good from the recycle bin.
  await page.getByRole('row', { name: new RegExp(second) }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Item actions' }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  await expect(page.getByRole('row', { name: new RegExp(second) })).toBeHidden();
  await page.getByRole('link', { name: 'Recycle bin' }).click();
  await page.getByRole('listitem').filter({ hasText: second }).getByRole('button', { name: 'Delete for good' }).click();
  await expect(page.getByText('The recycle bin is empty')).toBeVisible();
});

test('folders organize items and the search finds titles', async ({ page }) => {
  await createList(page, 'Documents', 'Files');
  await page.getByRole('button', { name: 'New folder' }).click();
  await page.getByLabel('Name').fill('Contracts');
  await page.getByRole('button', { name: 'Create folder' }).click();
  await page.getByRole('row', { name: /Contracts/ }).click();
  await expect(page.getByRole('navigation', { name: 'Folder' })).toContainText('Contracts');
  await expect(page.getByText('This folder is empty')).toBeVisible();

  await page.getByRole('navigation', { name: 'Folder' }).getByRole('button', { name: 'Files' }).click();
  await page.getByLabel('Search this list').fill('contr');
  await expect(page.getByRole('row', { name: /Contracts/ })).toBeVisible();
  await page.getByLabel('Search this list').fill('nothing like this');
  await expect(page.getByText('No matching items')).toBeVisible();
});

test('a task assigned with the people picker shows on Home and opens from there', async ({ page }) => {
  await createList(page, 'Tasks', 'Mine');
  const title = unique('Call the bank');
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(title);
  await panel.getByRole('combobox', { name: 'Assigned to' }).click();
  await page.getByRole('option', { name: /Administrator/ }).click();
  await page.keyboard.press('Escape');
  await expect(panel.getByRole('combobox', { name: 'Assigned to' })).toContainText('Administrator');
  await panel.getByRole('button', { name: 'Create' }).click();
  await expect(panel.getByRole('tab', { name: 'Details' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('row', { name: new RegExp(title) })).toContainText('Administrator');

  await page.getByRole('link', { name: 'Home' }).click();
  await page.getByRole('tab', { name: 'All mine' }).click();
  await page.getByRole('link', { name: new RegExp(title) }).click();
  await expect(page.getByRole('dialog').getByLabel('Title')).toHaveValue(title);
});
