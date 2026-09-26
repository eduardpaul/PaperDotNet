import { expect, test } from '@playwright/test';
import { createList, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

test('My tasks adds, opens and completes tasks with a checklist, subtasks and a repeat', async ({ page }) => {
  await createList(page, 'Tasks', 'Personal');
  await page.getByRole('link', { name: 'My tasks' }).click();
  const title = unique('Renew passport');
  await page.getByLabel('New task').fill(title);
  await page.getByLabel('Due date').fill('2030-03-01');
  const select = page.getByLabel('Task list');
  await select.selectOption((await select.locator('option', { hasText: 'Personal' }).last().getAttribute('value'))!);
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('button', { name: new RegExp(title) })).toBeVisible();

  await page.getByRole('button', { name: new RegExp(title) }).click();
  const panel = page.getByRole('dialog');
  await panel.getByRole('tab', { name: 'Checklist' }).click();
  for (const step of ['Find the old passport', 'Book an appointment']) {
    await panel.getByLabel('New checklist entry').fill(step);
    await panel.getByRole('button', { name: 'Add' }).click();
  }
  await panel.getByRole('checkbox', { name: 'Find the old passport' }).check();
  await expect(panel.getByText('1 of 2')).toBeVisible();

  await panel.getByRole('tab', { name: 'Related' }).click();
  await panel.getByLabel('New subtask').fill('Take photos');
  await panel.getByRole('button', { name: 'Add' }).click();
  await expect(panel.getByRole('link', { name: 'Take photos' })).toBeVisible();

  await panel.getByRole('tab', { name: 'Repeat' }).click();
  await panel.getByLabel('Frequency').selectOption('YEARLY');
  await panel.getByRole('button', { name: 'Save repeat' }).click();
  await expect(panel.getByText('Now: Every year')).toBeVisible();
  await page.keyboard.press('Escape');

  await page.getByRole('checkbox', { name: `Complete ${title}` }).check();
  await expect(page.getByText(`“${title}” done.`)).toBeVisible();
});

test('the calendar shows new and repeating events, and one occurrence can be skipped', async ({ page }) => {
  await createList(page, 'Calendar', 'Team calendar');
  await page.getByRole('link', { name: 'Calendar' }).first().click();
  await expect(page.getByRole('tab', { name: 'Month' })).toHaveAttribute('aria-selected', 'true');

  const title = unique('Standup');
  await page.getByRole('button', { name: 'New event' }).click();
  // With several calendars (other tests make some), the app asks which one.
  const which = page.getByRole('dialog', { name: 'Which calendar?' });
  if (await which.isVisible().catch(() => false))
    await which
      .getByRole('button', { name: /Team calendar/ })
      .last()
      .click();
  const panel = page.getByRole('dialog');
  // A day's new event starts at 09:00 in the user's time zone.
  await expect(panel.getByLabel('Start')).toHaveValue(/T09:00$/);
  await panel.getByLabel('Title').fill(title);
  await panel.getByRole('button', { name: 'Create' }).click();
  await panel.getByRole('tab', { name: 'Repeat' }).click();
  await panel.getByLabel('Frequency').selectOption('DAILY');
  await panel.getByLabel('Ends').selectOption('count');
  await panel.getByLabel('Occurrences').fill('3');
  await panel.getByRole('button', { name: 'Save repeat' }).click();
  await expect(panel.getByText('Now: Every day, 3 times')).toBeVisible();
  await page.keyboard.press('Escape');

  await page.getByRole('tab', { name: 'Agenda' }).click();
  const occurrences = page.getByRole('button', { name: new RegExp(title) });
  await expect(occurrences).toHaveCount(3);
  await occurrences.nth(1).click();
  await page.getByRole('button', { name: 'Skip this one' }).click();
  await expect(page.getByText('This occurrence is skipped.')).toBeVisible();
  await expect(occurrences).toHaveCount(2);
});

test('an .ics file is imported into a calendar', async ({ page }) => {
  await createList(page, 'Calendar', 'Imported');
  await page.getByRole('link', { name: 'Calendar' }).first().click();
  const uid = `${Date.now()}@example.com`;
  const summary = unique('Imported planning');
  const ics = [
    'BEGIN:VCALENDAR',
    'VERSION:2.0',
    'PRODID:-//Test//EN',
    'BEGIN:VEVENT',
    `UID:${uid}`,
    'DTSTAMP:20260101T000000Z',
    'DTSTART:20300115T100000Z',
    'DTEND:20300115T110000Z',
    `SUMMARY:${summary}`,
    'END:VEVENT',
    'END:VCALENDAR',
  ].join('\r\n');
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Import .ics' }).click();
  await (await chooser).setFiles({ name: 'plan.ics', mimeType: 'text/calendar', buffer: Buffer.from(ics) });
  const select = page.getByLabel('Into');
  await select.selectOption((await select.locator('option', { hasText: 'Imported' }).last().getAttribute('value'))!);
  await page.getByRole('button', { name: 'Import', exact: true }).click();
  await expect(page.getByText('Imported: 1 new, 0 updated.')).toBeVisible();

  await page.goto('/calendar?view=agenda&date=2030-01-10');
  await expect(page.getByRole('button', { name: new RegExp(summary) })).toBeVisible();
});

test('the approvals page lists decisions waiting for the user', async ({ page }) => {
  await page.getByRole('link', { name: 'Approvals' }).click();
  await expect(page.getByRole('heading', { name: 'Approvals' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Waiting for me' })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Approved' }).click();
  await expect(page).toHaveURL(/status=approved/);
});
