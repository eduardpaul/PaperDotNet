import assert from 'node:assert/strict';
import { test } from 'node:test';
import { all, fields, fieldsOf, ifMatch, isStatus, toArray } from '../dist/index.js';
import { adminClient, tasksList, unique } from './helpers.mjs';

const { client } = await adminClient();

test('global identities, relationships and moves work through the generated SDK', async () => {
  const source = await tasksList(client);
  const destination = await tasksList(client);
  const title = unique('SDK relationship');
  const first = await source.items.post({ fields: fields({ title, priority: 'high' }) });
  const second = await destination.items.post({ fields: fields({ title: `${title} target` }) });
  const global = client.api.v10.items;

  await global.byItemId(first.id).relations.byOtherId(second.id).put();
  await global.byItemId(second.id).relations.byOtherId(first.id).put();
  const related = await toArray(all(global.byItemId(first.id).relations, { queryParameters: { top: 1 } }));
  assert.equal(related.length, 1);
  assert.equal(related[0].item.id, second.id);
  assert.equal(related[0].canRelate, true);
  assert.equal(related[0].workspaceId, destination.workspace.id);

  const found = await global.get({ queryParameters: { q: title, writable: true, workspaceId: source.workspace.id } });
  assert.deepEqual(found.value.map((entry) => entry.item.id), [first.id]);
  const current = await global.byItemId(first.id).get();
  assert.ok(current.item.odataEtag);
  const moved = await global.byItemId(first.id).move.post(
    { workspaceId: destination.workspace.id, listId: destination.list.id },
    ifMatch(current.item),
  );
  assert.equal(moved.item.id, first.id);
  assert.equal(moved.item.listId, destination.list.id);
  assert.equal(moved.workspaceId, destination.workspace.id);
  assert.equal(fieldsOf(moved.item).priority, 'high');
  await assert.rejects(() => source.items.byItemId(first.id).get(), (error) => isStatus(error, 404));
  const inverse = await global.byItemId(second.id).relations.get();
  assert.equal(inverse.value[0].item.id, first.id);
  assert.equal(inverse.value[0].item.listId, destination.list.id);
  await global.byItemId(second.id).relations.byOtherId(first.id).delete();
  assert.deepEqual((await global.byItemId(first.id).relations.get()).value, []);
});
