// Documents: multipart upload, processing reported through live events, download, search; preferences with null reset.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { downloadFile, fieldsOf, ifMatch, subscribeLiveEvents, uploadBody } from '../dist/index.js';
import { adminClient, eventually, textPdf, unique } from './helpers.mjs';

const { client } = await adminClient();
const { api } = client;
const workspace = await api.v10.workspaces.post({ name: unique('SDK documents') });
const library = await api.v10.workspaces.byWorkspaceId(workspace.id).lists.post({ name: 'Documents', templateKey: 'documents' });
const libraryApi = api.v10.workspaces.byWorkspaceId(workspace.id).lists.byListId(library.id);

test('upload, processing events, download and search', async () => {
  const events = [];
  let connected;
  const isConnected = new Promise((resolve) => (connected = resolve));
  const subscription = subscribeLiveEvents(client, {
    connected: () => connected(),
    'document.processing': (e) => events.push(e),
    error: (e) => events.push({ error: e }),
  });

  try {
    await isConnected;
    const word = `zebra${Date.now().toString(36)}`;
    const pdf = textPdf(`The ${word} numbers are up.`);
    const file = new File([pdf], 'report.pdf', { type: 'application/pdf' });
    const uploaded = await libraryApi.documents.post(await uploadBody({ file, title: 'Quarterly report' }));
    assert.ok(uploaded.itemId);
    assert.equal(fieldsOf(uploaded).title, 'Quarterly report');
    assert.equal(uploaded.file.fileName, 'report.pdf');
    assert.equal(uploaded.file.mediaType, 'application/pdf');

    const done = await eventually(() => events.find((e) => e.itemId === uploaded.itemId && (e.status === 'succeeded' || e.status === 'failed')));
    assert.equal(done.status, 'succeeded', JSON.stringify(events));

    const fileApi = libraryApi.items.byItemId(uploaded.itemId).file;
    const bytes = await fileApi.get();
    assert.deepEqual(new Uint8Array(bytes), pdf);

    const download = await downloadFile(client, `/v1.0/workspaces/${workspace.id}/lists/${library.id}/items/${uploaded.itemId}/file`);
    assert.equal(download.fileName, 'report.pdf');
    assert.equal(download.contentType, 'application/pdf');
    assert.deepEqual(new Uint8Array(await download.blob.arrayBuffer()), pdf);

    const image = await fileApi.pages.byPage(1).image.get();
    assert.deepEqual([...new Uint8Array(image).subarray(0, 3)], [0xff, 0xd8, 0xff], 'page images are JPEG');

    const hits = await eventually(async () => {
      const result = await api.v10.search.get({ queryParameters: { q: word, workspaceId: workspace.id } });
      return result.value?.length ? result : undefined;
    });
    assert.equal(hits.value[0].id, uploaded.itemId);
  } finally {
    subscription.close();
  }
});

test('uploading a byte view and replacing the file', async () => {
  const first = textPdf('First version');
  const padded = new Uint8Array(first.length + 8);
  padded.set(first, 4);
  const uploaded = await libraryApi.documents.post(await uploadBody({ file: padded.subarray(4, 4 + first.length), fileName: 'notes.pdf' }));
  const fileApi = libraryApi.items.byItemId(uploaded.itemId).file;
  assert.deepEqual(new Uint8Array(await fileApi.get()), first, 'only the view is sent');

  const second = textPdf('Second version');
  await fileApi.put(await uploadBody({ file: second, fileName: 'notes.pdf' }));
  assert.deepEqual(new Uint8Array(await fileApi.get()), second);
  const versions = await fileApi.versions.get();
  assert.equal(versions.value?.length ?? versions.length, 2);
});

test('preferences: PATCH sets values, null goes back to the inherited default', async () => {
  const preferences = api.v10.me.preferences;
  const before = await preferences.get();
  const changed = await preferences.patch({ timeZone: 'Europe/Madrid', theme: 'dark' }, ifMatch(before));
  assert.equal(changed.timeZone, 'Europe/Madrid');
  assert.equal(changed.theme, 'dark');

  const reset = await preferences.patch({ timeZone: null }, ifMatch(changed));
  assert.equal(reset.theme, 'dark');
  assert.ok(reset.inherited.includes('timeZone'), JSON.stringify(reset.inherited));
});
