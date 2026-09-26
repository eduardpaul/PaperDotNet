import { expect, test } from '@playwright/test';
import { createList, createUser, signIn, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

test('owners rename the workspace and manage its members', async ({ page, request }) => {
  const listUrl = await createList(page, 'Tasks', 'Chores');
  const workspaceUrl = listUrl.replace(/\/l\/.*$/, '');
  const person = unique('member').replace(' ', '-');
  await createUser(request, person, `Member ${person}`);

  await page.goto(workspaceUrl);
  await page.getByRole('link', { name: 'Settings' }).click();
  await expect(page.getByRole('heading', { name: 'Workspace settings' })).toBeVisible();
  const name = unique('Renamed');
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Description').fill('Where the chores live');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Workspace saved.')).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name })).toBeVisible();

  await page.getByRole('link', { name: 'Members' }).click();
  await page.getByRole('combobox', { name: 'People' }).click();
  await page.getByRole('option', { name: new RegExp(`Member ${person}`) }).click();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByText(`Member ${person} added.`)).toBeVisible();

  const role = page.getByLabel(`Role of Member ${person}`);
  await expect(role).toHaveValue('member');
  await role.selectOption('visitor');
  await page.reload();
  await expect(role).toHaveValue('visitor');

  // The last owner stays an owner.
  const mine = page.getByRole('listitem').filter({ hasText: '(you)' }).getByRole('combobox');
  await mine.selectOption('member');
  await expect(page.getByText(/at least one owner/).first()).toBeVisible();
  await expect(mine).toHaveValue('owner');

  await page.getByRole('button', { name: `Remove Member ${person}` }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Remove' }).click();
  await expect(page.getByText('Member removed.')).toBeVisible();
  await expect(role).toBeHidden();
});

test('an automation notifies on new items, shows its runs and can be turned off', async ({ page }) => {
  test.setTimeout(45_000);
  const listUrl = await createList(page, 'Tasks', 'Inbox tasks');
  const workspaceUrl = listUrl.replace(/\/l\/.*$/, '');
  await page.goto(`${workspaceUrl}/settings/automations`);
  await page.getByRole('button', { name: 'New automation' }).click();

  const editor = page.getByRole('dialog');
  const name = unique('Tell me');
  await editor.getByLabel('Name', { exact: true }).fill(name);
  await editor.getByLabel('Trigger').selectOption('itemAdded');
  await editor.getByLabel('List', { exact: true }).selectOption('Inbox tasks');
  const step = editor.getByRole('region', { name: /Step 1/ });
  await step.getByLabel('Action').selectOption('notify');
  await step.getByRole('combobox', { name: 'Notify' }).click();
  await page.getByRole('option', { name: /creator/ }).click();
  await page.keyboard.press('Escape');
  await step.getByLabel('Title', { exact: true }).fill('New task: {title}');

  // A second step waits an hour; the JSON view shows it too.
  await editor.getByRole('button', { name: 'Add a step' }).click();
  await page.getByRole('menuitem', { name: /^Wait/ }).click();
  await editor
    .getByRole('region', { name: /Step 2/ })
    .getByLabel('Hours')
    .fill('1');
  await editor.getByRole('button', { name: 'JSON' }).click();
  await expect(editor.getByLabel('Automation JSON')).toHaveValue(/"type": "delay"/);
  await editor.getByRole('button', { name: 'JSON' }).click();
  await editor.getByRole('button', { name: 'Create automation' }).click();
  await expect(page.getByText('Automation created.')).toBeVisible();
  await expect(page.getByRole('button', { name: new RegExp(`^${name}`) })).toContainText(
    'When an item is added in Inbox tasks',
  );

  // A new task starts a run: the notification arrives and the run waits for its delay.
  await page.goto(listUrl);
  const title = unique('Buy milk');
  await page.getByRole('button', { name: 'New task' }).click();
  await page.getByRole('dialog').getByLabel('Title').fill(title);
  await page.getByRole('dialog').getByRole('button', { name: 'Create' }).click();
  await expect(page).toHaveURL(/item=[0-9a-f-]{36}/);
  // The run starts in the background.
  await expect(async () => {
    await page.goto('/notifications');
    await expect(page.getByText(`New task: ${title}`).first()).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 30_000 });

  await page.goto(`${workspaceUrl}/settings/runs`);
  const run = page.getByRole('button', { name: new RegExp(name) });
  await expect(run).toContainText('waiting');
  await run.click();
  await expect(page.getByRole('link', { name: 'Open the item' })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel run' }).click();
  await expect(page.getByText('Run cancelled.')).toBeVisible();
  await expect(run).toContainText('cancelled');

  await page.getByRole('link', { name: 'Automations' }).click();
  await page.getByRole('checkbox', { name: `${name} enabled` }).uncheck();
  await expect(page.getByRole('button', { name: new RegExp(`^${name}`) })).toContainText('Off');
});
