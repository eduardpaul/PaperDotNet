// Shared setup of the end-to-end tests: the server address and signed-in clients (see run.mjs).
import { OAuthSession, createPaperDotNetClient } from '../dist/index.js';

export const baseUrl = process.env.PAPERDOTNET_URL ?? 'http://127.0.0.1:5000';
export const adminPassword = process.env.PAPERDOTNET_ADMIN_PASSWORD ?? 'admin-password-dev';
export const redirectUri = 'http://localhost:5173/callback';

/** A client signed in as the administrator with the password grant. */
export async function adminClient() {
  const session = new OAuthSession({ baseUrl });
  await session.signInWithPassword('admin', adminPassword);
  return { session, client: createPaperDotNetClient({ baseUrl, auth: session }) };
}

/** A unique name, so tests can run against a server that is already in use. */
export function unique(prefix) {
  return `${prefix} ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}

/** A workspace with a Tasks list (fields: title, status, priority, percentComplete, dueDate, …). */
export async function tasksList(client) {
  const workspace = await client.api.v10.workspaces.post({ name: unique('SDK') });
  const list = await client.api.v10.workspaces.byWorkspaceId(workspace.id).lists.post({ name: 'Tasks', templateKey: 'tasks' });
  const items = client.api.v10.workspaces.byWorkspaceId(workspace.id).lists.byListId(list.id).items;
  return { workspace, list, items };
}

/** Waits until `check` returns a value (polling), or fails after `timeout` ms. */
export async function eventually(check, timeout = 30_000, interval = 250) {
  const started = Date.now();
  let lastError;
  while (Date.now() - started < timeout) {
    try {
      const value = await check();
      if (value) {
        return value;
      }
    } catch (error) {
      lastError = error;
    }

    await new Promise((r) => setTimeout(r, interval));
  }

  throw lastError ?? new Error('Timed out.');
}

/** A one-page PDF with a line of text (test data; real files come from users). */
export function textPdf(text) {
  const content = `BT /F1 18 Tf 72 720 Td (${text.replace(/[()\\]/g, '\\$&')}) Tj ET`;
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = objects.map((body, i) => {
    const offset = pdf.length;
    pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
    return offset;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return new TextEncoder().encode(pdf);
}
