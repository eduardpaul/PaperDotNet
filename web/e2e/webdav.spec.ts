import { expect, test } from '@playwright/test';
import { createList, signIn, textPdf, unique } from './helpers';

test.beforeEach(async ({ page }) => signIn(page));

test('a library shows its WebDAV address, and a token from the dialog opens it as a drive', async ({
  page,
  request,
}) => {
  const workspace = unique('Drive');
  await createList(page, 'Documents', 'Contracts', workspace);
  const chooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload' }).click();
  await (await chooser).setFiles({ name: 'lease.pdf', mimeType: 'application/pdf', buffer: textPdf('Lease') });
  await expect(page.getByRole('region', { name: 'Uploads' }).getByRole('link', { name: 'lease.pdf' })).toBeVisible();

  await page.getByRole('button', { name: 'Open in Explorer' }).click();
  const dialog = page.getByRole('dialog', { name: 'Open in Explorer' });
  const address = dialog.getByRole('textbox', { name: 'WebDAV address' });
  await expect(address).toHaveValue(/\/dav\/.+\/Contracts\/$/);
  const url = await address.inputValue();
  expect(decodeURIComponent(url)).toContain(`/dav/${workspace}/Contracts/`);
  await expect(dialog.getByRole('textbox', { name: 'Command' })).toHaveValue(`net use * "${url}" /user:paperdotnet *`);

  // The token dialog opens with the name and the read scopes WebDAV needs.
  await dialog.getByRole('link', { name: 'Create a token' }).click();
  const tokenDialog = page.getByRole('dialog', { name: 'New API token' });
  await expect(tokenDialog.getByLabel('Name', { exact: true })).toHaveValue('WebDAV');
  await expect(tokenDialog.getByRole('checkbox', { name: /^list\.read\b/ })).toBeChecked();
  await expect(tokenDialog.getByRole('checkbox', { name: /^document\.read\b/ })).toBeChecked();
  await tokenDialog.getByRole('button', { name: 'Create token' }).click();
  const secret = await page.getByRole('textbox', { name: 'API token' }).inputValue();

  // A WebDAV client lists the library with the token as the Basic password.
  const listing = await request.fetch(new URL(url).pathname, {
    method: 'PROPFIND',
    headers: { Depth: '1', Authorization: `Basic ${Buffer.from(`anyone:${secret}`).toString('base64')}` },
  });
  expect(listing.status()).toBe(207);
  expect(await listing.text()).toContain('lease.pdf');
});
