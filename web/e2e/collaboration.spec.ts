import { expect, test, type Page } from '@playwright/test';
import { createList, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

async function newNote(page: Page, title: string, body: string) {
  await page.getByRole('button', { name: 'New note' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(title);
  await panel.getByLabel('Body').fill(body);
  await panel.getByRole('button', { name: 'Create' }).click();
  await expect(page).toHaveURL(/item=[0-9a-f-]{36}/);
  return panel;
}

test('comments take @mentions, replies, edits and deletes, and items can be followed', async ({ page }) => {
  await createList(page, 'Tasks', 'Discussed');
  await page.getByRole('button', { name: 'New task' }).click();
  const panel = page.getByRole('dialog');
  await panel.getByLabel('Title').fill(unique('Plan the offsite'));
  await panel.getByRole('button', { name: 'Create' }).click();
  await expect(page).toHaveURL(/item=[0-9a-f-]{36}/);

  await panel.getByRole('button', { name: 'Follow', exact: true }).click();
  await expect(page.getByText('You follow this item')).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Following' })).toHaveAttribute('aria-pressed', 'true');

  await panel.getByRole('tab', { name: 'Activity' }).click();
  await expect(panel.getByRole('heading', { name: 'History' })).toBeVisible();
  const composer = panel.getByLabel('New comment');
  await composer.pressSequentially('Can you book it, @adm');
  await panel.getByRole('listbox', { name: 'People' }).getByRole('option').first().click();
  await expect(composer).toHaveValue(/^Can you book it, @\S.* $/);
  await composer.pressSequentially('please?');
  await panel.getByRole('button', { name: 'Comment', exact: true }).click();
  await expect(panel.getByText(/Can you book it/).first()).toBeVisible();
  await expect(composer).toHaveValue('');

  await panel.getByRole('button', { name: 'Reply', exact: true }).click();
  await panel.getByLabel('Reply', { exact: true }).fill('Booked.');
  await panel.getByRole('button', { name: 'Reply', exact: true }).last().click();
  await expect(panel.getByText('Booked.', { exact: true })).toBeVisible();

  const reply = () =>
    panel.getByRole('listitem').filter({ hasText: 'Booked' }).filter({ hasNotText: 'Can you book it' });
  await reply().getByRole('button', { name: 'Edit' }).click();
  await panel.getByLabel('Edit comment').fill('Booked for Friday.');
  await reply().getByRole('button', { name: 'Save' }).click();
  await expect(panel.getByText('Booked for Friday.')).toBeVisible();
  await expect(panel.getByText(/edited/)).toBeVisible();

  await reply().getByRole('button', { name: 'Delete' }).click();
  await expect(panel.getByText('Booked for Friday.')).toBeHidden();
  await expect(panel.getByText(/Can you book it/).first()).toBeVisible();

  await panel.getByRole('button', { name: 'Following' }).click();
  await expect(panel.getByRole('button', { name: 'Follow', exact: true })).toHaveAttribute('aria-pressed', 'false');
});

test('notes render Markdown with [[wiki links]] and show their backlinks', async ({ page }) => {
  await createList(page, 'Notes', 'Knowledge');
  const target = unique('Onboarding');
  await newNote(page, target, '# Welcome\n\nStart here.');
  await page.keyboard.press('Escape');

  const source = unique('Handbook');
  let panel = await newNote(page, source, `See [[${target}|the onboarding page]] and **read it**. #handbook`);
  await panel.getByRole('tab', { name: 'Preview' }).click();
  await expect(panel.getByRole('document').locator('strong')).toHaveText('read it');
  // Links resolve in the background once the note is saved.
  await expect(async () => {
    await page.reload();
    await page.getByRole('dialog').getByRole('tab', { name: 'Links' }).click();
    await expect(page.getByRole('dialog').getByRole('link', { name: 'the onboarding page' })).toBeVisible({
      timeout: 1000,
    });
  }).toPass({ timeout: 20_000 });

  panel = page.getByRole('dialog');
  await panel.getByRole('link', { name: 'the onboarding page' }).click();
  await expect(panel.getByLabel('Title')).toHaveValue(target);
  await panel.getByRole('tab', { name: 'Links' }).click();
  await expect(panel.getByRole('link', { name: source })).toBeVisible();
});
