// Lists and items through the generated client: fields, OData queries, paging, ETags and validation problems.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { all, fields, fieldsOf, ifMatch, isStatus, pages, problemOf, toArray, validationErrors } from '../dist/index.js';
import { adminClient, tasksList } from './helpers.mjs';

const { client } = await adminClient();
const { items } = await tasksList(client);

test('items are created with fields and read back as plain values', async () => {
  const created = await items.post({ fields: fields({ title: 'Write SDK', priority: 'high', percentComplete: 40, tags: undefined }) });
  assert.ok(created.id);
  assert.ok(created.odataEtag, 'bodies carry @odata.etag');
  const values = fieldsOf(created);
  assert.equal(values.title, 'Write SDK');
  assert.equal(values.priority, 'high');
  assert.equal(values.percentComplete, 40);
  assert.equal(values.status, 'notStarted', 'defaults are applied');
  assert.ok(created.createdAt instanceof Date);

  const read = await items.byItemId(created.id).get();
  assert.deepEqual(fieldsOf(read), values);
});

test('$filter, $orderby, $select, $count and paging', async () => {
  for (let i = 1; i <= 5; i++) {
    await items.post({ fields: fields({ title: `Paged ${i}`, percentComplete: i * 10, priority: i % 2 ? 'low' : 'normal' }) });
  }

  const filtered = await items.get({ queryParameters: { filter: "startswith(fields/title, 'Paged') and fields/priority eq 'low'", orderby: 'fields/percentComplete desc', count: true } });
  assert.deepEqual(filtered.value.map((i) => fieldsOf(i).title), ['Paged 5', 'Paged 3', 'Paged 1']);
  assert.equal(filtered.odataCount, 3);

  const selected = await items.get({ queryParameters: { filter: "fields/title eq 'Paged 2'", select: 'title' } });
  assert.deepEqual(Object.keys(fieldsOf(selected.value[0])), ['title']);

  const allPages = await toArray(pages(items, { queryParameters: { top: 2, filter: "startswith(fields/title, 'Paged')", orderby: 'fields/title' } }));
  assert.equal(allPages.length, 3);
  assert.ok(allPages[0].odataNextLink);
  const titles = (await toArray(all(items, { queryParameters: { top: 2, filter: "startswith(fields/title, 'Paged')", orderby: 'fields/title' } }))).map((i) => fieldsOf(i).title);
  assert.deepEqual(titles, ['Paged 1', 'Paged 2', 'Paged 3', 'Paged 4', 'Paged 5']);
});

test('updates merge fields, null removes a value, and a stale ETag is a 412 problem', async () => {
  const item = await items.post({ fields: fields({ title: 'Concurrency', percentComplete: 10, description: 'Some text' }) });
  const updated = await items.byItemId(item.id).patch({ fields: fields({ percentComplete: 20, description: null }) }, ifMatch(item));
  assert.equal(fieldsOf(updated).percentComplete, 20);
  assert.equal(fieldsOf(updated).title, 'Concurrency', 'other fields are kept');
  assert.equal(fieldsOf(updated).description, undefined, 'null removes the value');
  assert.notEqual(updated.odataEtag, item.odataEtag);

  const error = await items.byItemId(item.id).patch({ fields: fields({ percentComplete: 30 }) }, ifMatch(item)).then(() => undefined, (e) => e);
  assert.ok(isStatus(error, 412));
  assert.ok(problemOf(error).code, 'problems carry a code');
});

test('invalid values are a validation problem with messages per field', async () => {
  const error = await items.post({ fields: fields({ title: 'Invalid', percentComplete: 500, priority: 'urgent' }) }).then(() => undefined, (e) => e);
  assert.ok(isStatus(error, 400));
  const errors = validationErrors(error);
  assert.ok(Object.keys(errors).some((k) => k.includes('percentComplete')), JSON.stringify(errors));
  assert.ok(Object.keys(errors).some((k) => k.includes('priority')), JSON.stringify(errors));
});

test('a missing item is a 404 problem; deleting needs the ETag of the current version', async () => {
  const missing = await items.byItemId('00000000-0000-7000-8000-000000000000').get().then(() => undefined, (e) => e);
  assert.ok(isStatus(missing, 404));

  const item = await items.post({ fields: fields({ title: 'Delete me' }) });
  await items.byItemId(item.id).delete(ifMatch(item));
  assert.ok(isStatus(await items.byItemId(item.id).get().then(() => undefined, (e) => e), 404));
});
