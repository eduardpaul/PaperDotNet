import { expect, test, type Page } from '@playwright/test';
import { createList, signIn, textPdf, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

async function uploadAndWait(page: Page, name: string, ...pages: string[]) {
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload' }).click();
  await (await chooser).setFiles({ name, mimeType: 'application/pdf', buffer: textPdf(...pages) });
  await page.getByRole('region', { name: 'Uploads' }).getByRole('link', { name }).click();
  await expect(page.getByRole('dialog').getByText('Searchable')).toBeVisible({ timeout: 30_000 });
  await page.keyboard.press('Escape');
}

test('search finds the page of a document and opens it there', async ({ page }) => {
  await createList(page, 'Documents', 'Archive');
  const word = `zebra${Date.now().toString(36)}`;
  await uploadAndWait(page, `${unique('report')}.pdf`, 'Introduction', `The ${word} appears on page two`);

  // Indexing runs in the background after processing.
  await expect(async () => {
    await page.goto(`/search?q=${word}`);
    await expect(page.getByRole('listitem').filter({ hasText: 'Page 2' })).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 30_000 });
  await expect(page.locator('mark', { hasText: word })).toBeVisible();

  await page.getByRole('listitem').filter({ hasText: 'Page 2' }).click();
  await expect(page.getByRole('dialog', { name: 'Page 2' })).toBeVisible();

  // Facets narrow the results.
  await page.goto(`/search?q=${word}`);
  await page
    .getByRole('complementary', { name: 'Filters' })
    .getByRole('button', { name: /Archive/ })
    .click();
  await expect(page).toHaveURL(/list=/);
  await expect(page.getByRole('listitem').filter({ hasText: 'Page 2' })).toBeVisible();
});

test('the command palette searches as you type', async ({ page }) => {
  await createList(page, 'Tasks', 'Palette');
  const title = unique('Quarterly tax filing');
  await page.getByRole('button', { name: 'New task' }).click();
  await page.getByRole('dialog').getByLabel('Title').fill(title);
  await page.getByRole('dialog').getByRole('button', { name: 'Create' }).click();
  await expect(page.getByRole('dialog').getByRole('tab', { name: 'Details' })).toBeVisible();
  await page.keyboard.press('Escape');

  await expect(async () => {
    await page.keyboard.press('ControlOrMeta+k');
    await page.getByRole('combobox').fill(title.split(' ').at(-1)!);
    await expect(page.getByRole('option', { name: title })).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 30_000 });
  await page.getByRole('option', { name: title }).click();
  await expect(page.getByRole('dialog').getByLabel('Title')).toHaveValue(title);

  await page.keyboard.press('Escape');
  await page.keyboard.press('ControlOrMeta+k');
  await page.getByRole('combobox').fill('tax filing');
  await page.getByRole('option', { name: /Search for/ }).click();
  await expect(page).toHaveURL(/\/search\?q=tax\+filing|\/search\?q=tax%20filing/);
});

test('smart folders show matching items across lists, group them and classify dropped items', async ({ page }) => {
  await createList(page, 'Tasks', 'Folders');
  const urgent = unique('Urgent');
  const later = unique('Later');
  for (const [title, priority] of [
    [urgent, 'high'],
    [later, 'low'],
  ]) {
    await page.getByRole('button', { name: 'New task' }).click();
    const panel = page.getByRole('dialog');
    await panel.getByLabel('Title').fill(title!);
    await panel.getByLabel('Priority').selectOption(priority!);
    await panel.getByRole('button', { name: 'Create' }).click();
    await expect(panel.getByRole('tab', { name: 'Details' })).toBeVisible();
    await page.keyboard.press('Escape');
  }
  const listUrl = page.url();

  const name = unique('High priority');
  await page.getByRole('button', { name: 'New smart folder' }).click();
  await page.getByLabel('Name').fill(name);
  await page.getByRole('button', { name: 'Tasks', exact: true }).click();
  await page.getByLabel('Condition (optional)').fill("fields/priority eq 'high'");
  await page.getByRole('button', { name: 'Add a level' }).click();
  await page.getByLabel('Group level 1 field').fill('status');
  await page.getByRole('button', { name: 'Create folder' }).click();

  await expect(page.getByRole('heading', { name })).toBeVisible();
  await page.getByRole('button', { name: /notStarted|Not started/ }).click();
  await expect(page.getByRole('button', { name: new RegExp(urgent) })).toBeVisible();
  await expect(page.getByRole('button', { name: new RegExp(later) })).toBeHidden();

  // Dropping the other task on the folder sets its priority (the folder's eq condition).
  await page.goto(listUrl.split('?')[0]!);
  await page.getByRole('row', { name: new RegExp(later) }).dragTo(page.getByRole('link', { name }));
  await expect(page.getByText(`added to ${name}`)).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(later) })).toContainText('High');
});

test('an item is added to a smart folder from its menu', async ({ page }) => {
  const name = unique('Waiting');
  await page.getByRole('button', { name: 'New smart folder' }).click();
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Condition (optional)').fill("fields/status eq 'inProgress'");
  await page.getByRole('button', { name: 'Create folder' }).click();
  await expect(page.getByRole('heading', { name })).toBeVisible();

  await createList(page, 'Tasks', 'Menu');
  const title = unique('Review contract');
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(title);
  await panel.getByRole('button', { name: 'Create' }).click();
  await panel.getByRole('button', { name: 'Item actions' }).click();
  await page.getByRole('menuitem', { name: 'Add to smart folder…' }).click();
  await page.getByLabel('Smart folder', { exact: true }).selectOption({ label: name });
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByText(`Added to ${name}.`)).toBeVisible();
  // The open form takes the value the folder set, since it had no edits of its own.
  await expect(panel.getByLabel('Status')).toHaveValue('inProgress');
  await expect(panel.getByText('Unsaved changes')).toBeHidden();

  await page.keyboard.press('Escape');
  await page.getByRole('link', { name }).click();
  await expect(page.getByRole('button', { name: new RegExp(title) })).toBeVisible();
  const row = page.getByRole('listitem').filter({ hasText: title });
  await row.hover();
  await row.getByRole('button', { name: 'Take out' }).click();
  await expect(page.getByRole('button', { name: new RegExp(title) })).toBeHidden();
});
