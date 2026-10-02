// The workflow script contract (ADR-0037): every case runs with the SDK's runner (runWorkflowScript, in Node) and as a
// workflow's script step on the server (Jint), and both must give the same result, variables, log, errors and writes.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ScriptError, fieldsOf, fields, runWorkflowScript } from '../dist/index.js';
import { adminClient, eventually, unique } from './helpers.mjs';

const { client } = await adminClient();
const order = await client.api.v10.contentTypes.post({
  name: unique('Script order'),
  fields: [
    { name: 'amount', type: 'number' },
    { name: 'status', type: 'text' },
  ],
});
const line = await client.api.v10.contentTypes.post({
  name: unique('Script line'),
  fields: [
    { name: 'order', type: 'text' },
    { name: 'qty', type: 'number' },
    { name: 'price', type: 'number' },
  ],
});

/** A workspace with the lists Orders and Lines, the run's order, and lines (`order: '$item'` is the order's id). */
async function setup(scenario) {
  const workspace = await client.api.v10.workspaces.post({ name: unique('Scripts') });
  const lists = client.api.v10.workspaces.byWorkspaceId(workspace.id).lists;
  const orders = await lists.post({ name: 'Orders', contentTypeIds: [order.id] });
  const lines = await lists.post({ name: 'Lines', contentTypeIds: [line.id] });
  const item = await lists.byListId(orders.id).items.post({ fields: fields(scenario.item ?? { title: 'Order 1', amount: 10 }) });
  for (const values of scenario.lines ?? []) {
    await lists.byListId(lines.id).items.post({ fields: fields({ ...values, order: values.order === '$item' ? item.id : values.order }) });
  }

  const read = async (list) =>
    ((await lists.byListId(list.id).items.get({ queryParameters: { top: 100, orderby: 'fields/title' } })).value ?? []).map((i) => ({ ...fieldsOf(i), id: i.id }));
  return { workspace, orders, lines, item, read };
}

/** The SDK's runner. */
async function inNode(world, scenario) {
  const run = await runWorkflowScript(client, {
    workspaceId: world.workspace.id,
    code: scenario.code,
    item: { list: 'Orders', id: world.item.id },
    vars: scenario.vars,
  });
  return { result: run.result, vars: run.vars, log: run.log };
}

/** A workflow with one script step, started on the order, on the server. */
async function onServer(world, scenario) {
  const base = `${client.baseUrl}/v1.0/workspaces/${world.workspace.id}`;
  const post = async (url, body) => {
    const response = await client.fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const text = await response.text();
    assert.ok(response.ok, `${url}: ${response.status} ${text}`);
    return JSON.parse(text);
  };
  const workflow = await post(`${base}/workflows`, {
    name: unique('Script'),
    trigger: { type: 'manual', list: 'Orders' },
    variables: scenario.vars,
    flow: { start: 'run', nodes: { run: { activity: 'script', inputs: { code: scenario.code } } } },
  });
  const started = await post(`${base}/lists/${world.orders.id}/items/${world.item.id}/workflows`, { workflow: workflow.name });
  const run = await eventually(async () => {
    const current = await (await client.fetch(`${base}/workflows/runs/${started.id}`)).json();
    return ['completed', 'failed'].includes(current.status) ? current : undefined;
  }, 60_000);
  if (run.status === 'failed') {
    throw new ScriptError(run.error);
  }

  const log = run.log.map((l) => l.message).filter((m) => m.startsWith('run: ') && !m.startsWith('run: script done'));
  return { result: run.outputs.run.result ?? null, vars: run.variables, log: log.map((m) => m.slice('run: '.length)) };
}

const cases = [
  {
    name: 'a script reads the run item and lists, and its planned writes are applied after it',
    item: { title: 'Order 1', amount: 10 },
    lines: [{ title: 'Old', order: '$item', qty: 1 }],
    vars: { rate: 2 },
    code: [
      "const old = await items.query('Lines', { filter: `fields/order eq '${item.id}'` });",
      "for (const l of old) await items.delete('Lines', l.id);",
      'const ids = [];',
      "for (const qty of [1, 2]) ids.push(await items.create('Lines', { title: `L${qty}`, order: item.id, qty, price: qty * item.amount }));",
      "await items.update('Orders', item.id, { status: 'priced', amount: item.amount * vars.rate });",
      'vars.priced = ids.length;',
      'log(`priced ${ids.length} lines of ${item.title}`);',
      "return { title: item.title, list: item.list, ids: ids.every((id) => /^[0-9a-f-]{36}$/.test(id)), seen: (await items.query('Lines')).length };",
    ],
    expect: {
      result: { title: 'Order 1', list: 'Orders', ids: true, seen: 1 },
      vars: { rate: 2, priced: 2 },
      log: ['priced 2 lines of Order 1'],
      lines: [
        { title: 'L1', qty: 1, price: 10 },
        { title: 'L2', qty: 2, price: 20 },
      ],
      order: { status: 'priced', amount: 20 },
    },
  },
  {
    name: 'get finds an item by id, and null when there is none',
    code: [
      "const same = await items.get('Orders', item.id);",
      "const none = await items.get('Orders', '00000000-0000-7000-8000-000000000000');",
      'return { title: same.title, amount: same.amount, id: same.id === item.id, none };',
    ],
    expect: { result: { title: 'Order 1', amount: 10, id: true, none: null } },
  },
  {
    name: 'query filters, orders and limits',
    lines: [1, 2, 3, 4].map((qty) => ({ title: `L${qty}`, order: 'x', qty })),
    code: [
      "const top = await items.query('Lines', { orderBy: 'fields/qty desc', top: 2 });",
      "const small = await items.query('Lines', { filter: 'fields/qty lt 3', orderBy: 'fields/qty' });",
      'return { top: top.map((l) => l.title), small: small.map((l) => l.qty), list: top[0].list };',
    ],
    expect: { result: { top: ['L4', 'L3'], small: [1, 2], list: 'Lines' } },
  },
  {
    name: 'a script without return has a null result, and a write of nothing changes nothing',
    code: ["await items.update('Orders', item.id, {});"],
    expect: { result: null, order: { amount: 10 } },
  },
  { name: 'an unknown list fails the script', code: ['const a = 1;', "await items.query('Nope');"], error: "The list 'Nope' does not exist in the workspace." },
  { name: 'a failed call fails the script even when it is not awaited', code: ["items.get('Nope', 'x').catch(() => null);", "await items.create('Lines', { title: 'Kept?' });", 'return 1;'], error: "The list 'Nope' does not exist in the workspace." },
  { name: 'ids must be item ids', code: ["await items.update('Orders', 'abc', { status: 'x' });"], error: 'id must be the id of an item.' },
  { name: 'fields must be an object', code: ["await items.create('Lines', [1, 2]);"], error: 'fields must be an object.' },
  { name: 'query options are checked', code: ["await items.query('Lines', { top: 'all' });"], error: 'top must be a number.' },
  { name: 'reads are limited', code: ["for (let i = 0; i < 201; i++) await items.get('Orders', item.id);"], error: 'A script reads at most 200 times.' },
  { name: 'a thrown error fails the script with its line', code: ["await items.create('Lines', { title: 'Not kept' });", "throw new Error('boom');"], error: 'boom (line 2)' },
  { name: 'a write the list rejects fails the script', code: ["await items.create('Lines', { title: 'Bad', qty: 'many' });"], error: 'write 1 (create in Lines)' },
];

for (const scenario of cases) {
  for (const runner of [inNode, onServer]) {
    test(`${scenario.name} (${runner === inNode ? 'SDK' : 'server'})`, async () => {
      const world = await setup(scenario);
      const linesBefore = await world.read(world.lines);
      if (scenario.error) {
        const error = await runner(world, scenario).then(() => undefined, (e) => e);
        assert.ok(error instanceof ScriptError, `expected the script to fail with "${scenario.error}"`);
        assert.ok(error.message.includes(scenario.error), `"${error.message}" should include "${scenario.error}"`);
        assert.deepEqual(await world.read(world.lines), linesBefore, 'a failed script applies none of its writes');
        return;
      }

      const outcome = await runner(world, scenario);
      assert.deepEqual(outcome.result, scenario.expect.result);
      assert.deepEqual(outcome.vars, scenario.expect.vars ?? {});
      assert.deepEqual(outcome.log, scenario.expect.log ?? []);
      const pick = (values, like) => Object.fromEntries(Object.keys(like).map((key) => [key, values[key]]));
      if (scenario.expect.lines) {
        const lines = await world.read(world.lines);
        assert.deepEqual(lines.map((l, i) => pick(l, scenario.expect.lines[i] ?? {})), scenario.expect.lines);
        assert.ok(lines.every((l) => l.order === world.item.id));
      }

      if (scenario.expect.order) {
        const [current] = await world.read(world.orders);
        assert.deepEqual(pick(current, scenario.expect.order), scenario.expect.order);
      }
    });
  }
}

for (const runner of [inNode, onServer]) {
  test(`typed graph scripts create, read and remove links (${runner === inNode ? 'SDK' : 'server'})`, async () => {
    const world = await setup({});
    const predicate = unique('Script contains');
    await client.api.v10.relationshipTypes.post({ name: predicate, directed: true, inverseLabel: 'belongs to', maxIncoming: 1 });
    const create = await runner(world, { code: [
      "const id = await items.create('Lines', { title: 'Graph line', qty: 2, price: 3 });",
      `await items.relate(item.id, id, ${JSON.stringify(predicate)});`,
      `await items.relate(item.id, id, ${JSON.stringify(predicate)});`,
      'return true;',
    ] });
    assert.equal(create.result, true);
    const edges = await client.api.v10.items.byItemId(world.item.id).relationships.get();
    assert.equal(edges.value.length, 1);
    assert.equal(edges.value[0].directed, true);
    const peer = edges.value[0].targetItemId;
    const remove = await runner(world, { code: [
      `const page = await items.related(item.id, { type: ${JSON.stringify(predicate)}, direction: 'outgoing' });`,
      'for (const edge of page.value) { await items.unrelate(item.id, edge.id); await items.deleteById(edge.item.id); }',
      'return { count: page.value.length, cursor: page.nextCursor };',
    ] });
    assert.deepEqual(remove.result, { count: 1, cursor: null });
    assert.equal((await client.api.v10.items.byItemId(world.item.id).relationships.get()).value.length, 0);
    await assert.rejects(() => client.api.v10.items.byItemId(peer).get());
  });
}
