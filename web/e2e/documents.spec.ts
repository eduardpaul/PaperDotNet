import { expect, test } from '@playwright/test';
import { createList, expectTextRead, signIn, textPdf, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

const pdf = (name: string, ...pages: string[]) => ({ name, mimeType: 'application/pdf', buffer: textPdf(...pages) });

test('documents are uploaded into a library, read by its workflows live and previewed page by page', async ({
  page,
}) => {
  await createList(page, 'Documents', 'Contracts');
  const name = `${unique('lease')}.pdf`;
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload' }).click();
  await (await chooser).setFiles(pdf(name, 'Lease agreement page one', 'Lease agreement page two'));

  const tray = page.getByRole('region', { name: 'Uploads' });
  await expect(tray.getByRole('link', { name })).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(name.replace('.pdf', '')) })).toBeVisible();

  await tray.getByRole('link', { name }).click();
  const panel = page.getByRole('dialog');
  await expect(panel.getByRole('tab', { name: 'Preview' })).toHaveAttribute('aria-selected', 'true');
  // The library's workflows run in the background; live events update the panel without a reload.
  const workflows = panel.getByRole('region', { name: 'Workflows' });
  await expect(workflows.getByRole('listitem').filter({ hasText: 'Read the text (Contracts)' })).toContainText('Done', {
    timeout: 30_000,
  });
  await expect(workflows.getByRole('listitem').filter({ hasText: 'Render pages (Contracts)' })).toContainText('Done');
  await expect(panel.getByText('2 pages')).toBeVisible();
  await expect(panel.getByRole('img', { name: 'Page 2' })).toBeVisible();

  const download = page.waitForEvent('download');
  await panel.getByRole('button', { name: 'Download' }).click();
  expect((await download).suggestedFilename()).toBe(name);
});

test('pages are rotated, reordered and split off into a new document', async ({ page }) => {
  await createList(page, 'Documents', 'Scans');
  const name = `${unique('mixed')}.pdf`;
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload' }).click();
  await (await chooser).setFiles(pdf(name, 'Invoice', 'Receipt', 'Letter'));
  const tray = page.getByRole('region', { name: 'Uploads' });
  await tray.getByRole('link', { name }).click();
  const panel = page.getByRole('dialog');
  await expect(panel.getByText('3 pages')).toBeVisible({ timeout: 30_000 });

  await panel.getByRole('checkbox', { name: 'Select page 1' }).check();
  await panel.getByRole('button', { name: 'Rotate right' }).click();
  await panel.getByRole('button', { name: 'Move later' }).click();
  await expect(panel.getByRole('button', { name: 'Page 2 (was 1)' })).toBeVisible();
  await panel.getByRole('button', { name: 'Save pages' }).click();
  // The upload, then the page edit (OCR is off by default, so no OCR version comes in between).
  await expect(panel.getByRole('listitem').filter({ hasText: 'Version 2' })).toContainText('Current');
  await expect(panel.getByRole('button', { name: 'Page 1', exact: true })).toBeVisible();

  await panel.getByRole('checkbox', { name: 'Select page 3' }).check();
  await panel.getByRole('button', { name: 'Split off' }).click();
  await expect(panel.getByText('2 pages')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('row')).toHaveCount(3); // header + original + the split-off document
});

test('the Inbox takes dropped files and documents are filed into a library', async ({ page }) => {
  await createList(page, 'Documents', 'Finance');
  await page.getByRole('link', { name: 'Inbox' }).click();
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible();

  const name = `${unique('bill')}.pdf`;
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload' }).click();
  await (await chooser).setFiles(pdf(name, 'Electricity bill'));
  const title = name.replace('.pdf', '');
  await page.getByRole('button', { name: new RegExp(title) }).click();

  const panel = page.getByRole('dialog');
  await expectTextRead(panel);
  await panel.getByRole('button', { name: 'Item actions' }).click();
  await page.getByRole('menuitem', { name: 'File in a library…' }).click();
  const library = page.getByLabel('Library', { exact: true });
  await library.selectOption((await library.locator('option', { hasText: 'Finance' }).getAttribute('value'))!);
  await page.getByRole('button', { name: 'File', exact: true }).click();
  await expect(page.getByText('Filed in Finance.')).toBeVisible();
  await expect(page.getByRole('button', { name: new RegExp(title) })).toBeHidden();

  await page.getByRole('button', { name: 'Open' }).click();
  await expect(page.getByRole('dialog').getByLabel('Title')).toHaveValue(title);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('heading', { name: 'Finance' })).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(title) })).toBeVisible();
});

test('uploading the same file twice warns about the duplicate', async ({ page }) => {
  await createList(page, 'Documents', 'Duplicates');
  const file = pdf(`${unique('same')}.pdf`, 'Exactly the same content');
  for (let i = 0; i < 2; i++) {
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Upload' }).click();
    await (await chooser).setFiles(file);
  }
  await expect(page.getByRole('region', { name: 'Uploads' }).getByText(/Same file as/)).toBeVisible();
});

test('unsupported files are refused with a clear message', async ({ page }) => {
  await createList(page, 'Documents', 'Refused');
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload' }).click();
  await (await chooser).setFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('plain text') });
  await expect(page.getByRole('region', { name: 'Uploads' })).toContainText(/PDF|supported|type/i);
});
