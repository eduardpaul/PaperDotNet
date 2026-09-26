import { expect, test, type Page } from '@playwright/test';
import { createList, createUser, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

async function newTask(page: Page, title: string, priority: string) {
  await page.getByRole('button', { name: /^New (task|item)$/ }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(title);
  await panel.getByLabel('Priority').selectOption(priority);
  await panel.getByRole('button', { name: 'Create' }).click();
  await expect(page).toHaveURL(/item=[0-9a-f-]{36}/);
  await page.keyboard.press('Escape');
}

test('list settings rename the list, keep versions, add columns through a content type and save views', async ({
  page,
}) => {
  const listUrl = await createList(page, 'Tasks', 'Project');
  await newTask(page, 'Pour the foundation', 'high');
  await newTask(page, 'Paint the fence', 'low');

  await page.getByRole('link', { name: 'List settings' }).click();
  await expect(page.getByRole('heading', { name: 'List settings' })).toBeVisible();
  const name = unique('House');
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Version history').selectOption('major');
  await page.getByLabel('Versions to keep').fill('10');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Settings saved.')).toBeVisible();

  // Columns come from content types: a new one adds Budget and Phase to this list.
  await page.getByRole('link', { name: 'Columns' }).click();
  await expect(page.getByRole('row', { name: /Priority/ })).toBeVisible();
  await page.getByRole('button', { name: 'New content type' }).click();
  const editor = page.getByRole('dialog');
  const typeName = unique('Building task');
  await editor.getByLabel('Name', { exact: true }).fill(typeName);
  await editor.getByRole('button', { name: 'Add a field' }).click();
  let field = editor.getByRole('region', { name: 'Field New field' });
  await field.getByLabel('Display name').fill('Budget');
  field = editor.getByRole('region', { name: 'Field Budget' });
  await expect(field.getByLabel('Name', { exact: true })).toHaveValue('budget');
  await field.getByLabel('Type').selectOption('currency');
  await field.getByLabel('Currency').fill('EUR');
  await editor.getByRole('button', { name: 'Add a field' }).click();
  field = editor.getByRole('region', { name: 'Field New field' });
  await field.getByLabel('Display name').fill('Build phase');
  field = editor.getByRole('region', { name: 'Field Build phase' });
  await field.getByLabel('Type').selectOption('choice');
  await field.getByLabel('Choices').fill('Plan\nBuild\nDone');
  await editor.getByRole('button', { name: 'Create content type' }).click();
  await expect(page.getByText('Content type created.')).toBeVisible();
  await expect(page.getByRole('row', { name: /Budget budget Money/ })).toBeVisible();
  await expect(page.getByRole('row', { name: /Build phase buildPhase Choice/ })).toBeVisible();

  // A view with only the important tasks, shown first.
  await page.getByRole('link', { name: 'Views' }).click();
  await page.getByRole('button', { name: 'New view' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name').fill('Important');
  await dialog.getByLabel('Only items where').fill("fields/priority eq 'high'");
  await dialog.getByLabel('Sort by').selectOption('title');
  await dialog.getByLabel('Show this view first').check();
  await dialog.getByRole('button', { name: 'Create view' }).click();
  await expect(page.getByText('View created.')).toBeVisible();

  await page.goto(listUrl);
  await expect(page.getByRole('heading', { name })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Important' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('row', { name: /Pour the foundation/ })).toBeVisible();
  await expect(page.getByRole('row', { name: /Paint the fence/ })).toBeHidden();
});

test('a list gets its own permissions, and an item too', async ({ page, request }) => {
  const reader = unique('reader').replace(' ', '-');
  await createUser(request, reader, `Reader ${reader}`);
  await createList(page, 'Tasks', 'Private');
  await newTask(page, 'Secret plan', 'normal');
  await page.getByRole('link', { name: 'List settings' }).click();
  await page.getByRole('link', { name: 'Permissions' }).click();

  await expect(page.getByText(/inherits its permissions/)).toBeVisible();
  await page.getByRole('button', { name: 'Give it its own permissions' }).click();
  await expect(page.getByText('This list has its own permissions.')).toBeVisible();
  await page.getByRole('combobox', { name: 'People or groups' }).click();
  await page.getByRole('option', { name: new RegExp(`Reader ${reader}`) }).click();
  await page.keyboard.press('Escape');
  await page.getByLabel('Access to give').selectOption('read');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Permissions saved.')).toBeVisible();
  await page.reload();
  await expect(page.getByLabel(`Access of Reader ${reader}`, { exact: true })).toHaveValue('read');

  await page.getByRole('button', { name: 'Inherit again' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Inherit again' }).click();
  await expect(page.getByText(/inherits its permissions/)).toBeVisible();

  // The item panel has the same editor.
  await page.getByRole('link', { name: 'Private' }).first().click();
  await page.getByRole('row', { name: /Secret plan/ }).click();
  const panel = page.getByRole('dialog');
  await panel.getByRole('tab', { name: 'Access' }).click();
  await panel.getByRole('button', { name: 'Give it its own permissions' }).click();
  await expect(panel.getByText('This item has its own permissions.')).toBeVisible();
});

test('library settings choose how duplicates and OCR are handled', async ({ page }) => {
  await createList(page, 'Documents', 'Scans');
  await page.getByRole('link', { name: 'List settings' }).click();
  await expect(page.getByRole('heading', { name: 'Library settings' })).toBeVisible();
  await page.getByRole('link', { name: 'Documents' }).click();
  await page.getByLabel('Same file again').selectOption('block');
  await page.getByLabel('OCR languages').fill('deu+eng');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Settings saved.')).toBeVisible();
  await page.reload();
  await expect(page.getByLabel('Same file again')).toHaveValue('block');
  await expect(page.getByLabel('OCR languages')).toHaveValue('deu+eng');
});
