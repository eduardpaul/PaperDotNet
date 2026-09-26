import { expect, type Page } from '@playwright/test';

export const adminUser = 'admin';
export const adminPassword = process.env.PAPERDOTNET_ADMIN_PASSWORD ?? 'admin-password-e2e';

/** A name no other test uses. */
export function unique(prefix: string): string {
  return `${prefix} ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}

/** Signs in through the UI: the app sends the browser to the server's authorization endpoint, which shows /login. */
export async function signIn(page: Page, path = '/', userName = adminUser, password = adminPassword) {
  await page.goto(path);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.getByLabel('User name or email').fill(userName);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Account' })).toBeVisible();
}

/** Creates a workspace and a list from a template through the UI; returns the list page URL. */
export async function createList(page: Page, template: string, name: string) {
  await page.goto('/w?create=true');
  await page.getByLabel('Name').fill(unique('Workspace'));
  await page.getByRole('button', { name: 'Create workspace' }).click();
  await page.getByRole('button', { name: 'New list' }).first().click();
  await page.getByRole('radio', { name: new RegExp(`^${template}`) }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page.getByRole('heading', { name })).toBeVisible();
  return page.url();
}

/** A small PDF with one line of text per page (text layer, so processing needs no OCR). */
export function textPdf(...pages: string[]): Buffer {
  const pageIds = pages.map((_, i) => 3 + i * 2);
  const objects: string[] = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    `<< /Type /Pages /Kids [${pageIds.map((id) => `${id} 0 R`).join(' ')}] /Count ${pages.length} >>`,
  ];
  const font = 3 + pages.length * 2;
  pages.forEach((text, i) => {
    const content = `BT /F1 24 Tf 72 700 Td (${text.replace(/[()\\]/g, '\\$&')}) Tj ET`;
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents ${4 + i * 2} 0 R /Resources << /Font << /F1 ${font} 0 R >> >> >>`,
      `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    );
  });
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  let pdf = '%PDF-1.4\n';
  const offsets = objects.map((body, i) => {
    const offset = pdf.length;
    pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
    return offset;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}
