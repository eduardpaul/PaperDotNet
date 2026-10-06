import type { WorkflowResponse } from '@paperdotnet/client';
import { describe, expect, it } from 'vitest';
import { describeTriggers, draftFrom, fromPlain, requestFrom, toPlain, manualWorkflows } from './model';

const flow = {
  start: 'big?',
  nodes: {
    'big?': { activity: 'if', inputs: { left: '{amount}', op: 'gt', right: '{var:limit}' }, next: { true: 'note' } },
    note: { activity: 'item.update', inputs: { fields: { note: 'big' } } },
  },
};

describe('workflow model', () => {
  it('keeps a flow and its variables through the JSON view and back to a request', () => {
    const draft = fromPlain({ name: 'Big bills', trigger: { type: 'manual' }, flow, variables: { limit: 100 } });
    expect(toPlain(draft)).toMatchObject({ flow, variables: { limit: 100 } });
    expect(toPlain(draft)).not.toHaveProperty('steps');

    const request = requestFrom(draft);
    expect(request.flow?.start).toBe('big?');
    expect(request.flow?.nodes?.additionalData).toEqual(flow.nodes);
    expect(request.variables?.additionalData).toEqual({ limit: 100 });
    expect(request.steps).toBeUndefined();
  });

  it('reads a flow from the API', () => {
    const response = {
      name: 'Big bills',
      trigger: { type: 'manual' },
      flow: { start: 'big?', nodes: { additionalData: flow.nodes } },
      variables: { additionalData: { limit: 100 } },
    } as unknown as WorkflowResponse;
    const draft = draftFrom(response);
    expect(draft.flow).toEqual(flow);
    expect(draft.variables).toEqual({ limit: 100 });
  });

  it('keeps the process role a copy fills, so saving it keeps replacing the built-in', () => {
    const response = {
      name: 'Index for search (custom)',
      trigger: { type: 'manual' },
      flow: { start: 'big?', nodes: { additionalData: flow.nodes } },
      provides: 'search.index',
    } as unknown as WorkflowResponse;
    const draft = draftFrom(response);
    expect(draft.provides).toBe('search.index');
    expect(toPlain(draft)).toMatchObject({ provides: 'search.index' });
    expect(requestFrom(draft).provides).toBe('search.index');
  });

  it('keeps concurrency from the API through the JSON view to a request', () => {
    const response = {
      name: 'Once per item',
      trigger: { type: 'itemUpdated' },
      steps: [],
      concurrency: 'skip',
    } as unknown as WorkflowResponse;
    const draft = draftFrom(response);
    expect(toPlain(draft).concurrency).toBe('skip');
    expect(requestFrom(draft).concurrency).toBe('skip');
    expect(toPlain(fromPlain({ name: 'Default', trigger: { type: 'manual' } }))).not.toHaveProperty('concurrency');
  });

  it('preserves a trigger concurrency override alongside snapshot conditions', () => {
    const trigger = {
      type: 'itemUpdated',
      list: 'Receipts',
      concurrency: 'skip',
      parameters: { when: { target: 'tags', operator: 'added', term: 'Receipts/Tags/ticket' } },
    };
    const draft = fromPlain({ name: 'Read receipts', trigger });
    expect(toPlain(draft).trigger).toMatchObject(trigger);
    const request = requestFrom(draft);
    expect(request.trigger?.concurrency).toBe('skip');
    expect(toPlain(draftFrom(request as WorkflowResponse)).trigger).toMatchObject(trigger);
  });

  it('keeps several triggers from the API through the JSON view to a request, and describes them', () => {
    const response = {
      name: 'Read receipts',
      triggers: [
        { type: 'itemUpdated', list: 'Receipts', changedFields: ['tags'], contentType: null },
        { type: 'document.processed', list: 'Receipts', data: { additionalData: { hasText: false } } },
      ],
      steps: [],
    } as unknown as WorkflowResponse;
    const draft = draftFrom(response);
    const plain = toPlain(draft);
    expect(plain).not.toHaveProperty('trigger');
    expect(plain.triggers).toEqual([
      { type: 'itemUpdated', list: 'Receipts', changedFields: ['tags'] },
      { type: 'document.processed', list: 'Receipts', data: { hasText: false } },
    ]);
    expect(toPlain(fromPlain(plain)).triggers).toEqual(plain.triggers);
    const request = requestFrom(draft);
    expect(request.trigger).toBeUndefined();
    expect(request.triggers?.[1]?.data?.additionalData).toEqual({ hasText: false });
    expect(describeTriggers(response)).toBe(
      'When an item changes in Receipts, or When a document is processed in Receipts',
    );
  });

  it('sends steps when there is no flow', () => {
    const draft = fromPlain({ name: 'Notify', trigger: { type: 'itemAdded' }, steps: [{ type: 'delay', hours: 2 }] });
    const request = requestFrom(draft);
    expect(request.flow).toBeUndefined();
    expect(request.steps).toEqual([{ type: 'delay', hours: 2 }]);
  });

  it('keeps trigger settings: schedules, dates, terms and inputs', () => {
    const inputs = { properties: { label: { type: 'string' } }, required: ['label'] };
    const manual = requestFrom(
      fromPlain({ name: 'Stamp', trigger: { type: 'manual', list: 'Papers', terms: ['Docs/Tags/Receipt'], inputs } }),
    );
    expect(manual.trigger?.terms).toEqual(['Docs/Tags/Receipt']);
    expect(manual.trigger?.inputs?.additionalData).toEqual(inputs);

    const schedule = toPlain(
      fromPlain({
        name: 'Morning',
        trigger: { type: 'schedule', cron: '0 8 * * 1-5', timeZone: 'Europe/Berlin', list: 'X' },
      }),
    );
    expect(schedule.trigger).toMatchObject({
      type: 'schedule',
      cron: '0 8 * * 1-5',
      timeZone: 'Europe/Berlin',
      list: null,
    });
    expect(schedule.trigger).not.toHaveProperty('field');

    const date = toPlain(
      fromPlain({ name: 'Due', trigger: { type: 'date', list: 'Tasks', field: 'dueDate', offsetHours: -24 } }),
    );
    expect(date.trigger).toMatchObject({ type: 'date', list: 'Tasks', field: 'dueDate', offsetHours: -24 });
    expect(date.trigger).not.toHaveProperty('cron');
  });
});

describe('manual workflow selection', () => {
  const workflows: WorkflowResponse[] = [
    { id: 'workspace', enabled: true, scope: 'workspace', triggers: [{ type: 'schedule' }, { type: 'manual' }] },
    { id: 'list', enabled: true, scope: 'list', trigger: { type: 'manual', list: 'Invoices' } },
    { id: 'disabled', enabled: false, trigger: { type: 'manual' } },
    { id: 'automatic', enabled: true, trigger: { type: 'itemAdded' } },
    { id: 'library', enabled: true, listId: 'other', trigger: { type: 'manual' } },
  ];
  it('offers workspace launches without item workflows', () => {
    expect(manualWorkflows(workflows).map((w) => w.id)).toEqual(['workspace']);
  });
  it('offers matching list workflows and excludes workspace launches', () => {
    expect(manualWorkflows(workflows, { id: 'invoices', name: 'Invoices' }).map((w) => w.id)).toEqual(['list']);
    expect(manualWorkflows(workflows, { id: 'tasks', name: 'Tasks' })).toEqual([]);
  });
  it('preserves scope and the launch schema through editor round trips', () => {
    const workflow: WorkflowResponse = {
      scope: 'workspace',
      inputSchema: { additionalData: { type: 'object', properties: { flag: { type: 'boolean' } } } },
      trigger: { type: 'manual' },
    };
    const request = requestFrom(draftFrom(workflow));
    expect(request.scope).toBe('workspace');
    expect(request.inputSchema?.additionalData).toEqual(workflow.inputSchema?.additionalData);
  });
});

it('keeps original trigger input schemas when saving an existing manual workflow', () => {
  const response: WorkflowResponse = {
    trigger: {
      type: 'manual',
      inputs: { additionalData: { properties: { label: { type: 'string' } }, required: ['label'] } },
    },
  };
  expect(requestFrom(draftFrom(response)).trigger?.inputs?.additionalData).toEqual(
    response.trigger?.inputs?.additionalData,
  );
});

it('preserves workspace event filters and removes empty form entries', () => {
  const draft = fromPlain({
    name: 'Receipt tags',
    scope: 'workspace',
    trigger: { type: 'itemUpdated', contentType: 'Paper', terms: [' Documents/Tags/Receipt ', ''] },
  });
  const request = requestFrom(draft);
  expect(request.scope).toBe('workspace');
  expect(request.trigger?.list).toBeNull();
  expect(request.trigger?.contentType).toBe('Paper');
  expect(request.trigger?.terms).toEqual(['Documents/Tags/Receipt']);
  expect(describeTriggers(request)).toBe('When an item changes (Paper)');
});

it('keeps approval form schemas through SDK and editor round trips', () => {
  const schema = { type: 'object', properties: { amount: { type: 'integer', minimum: 1 } }, required: ['amount'] };
  const request = requestFrom(
    fromPlain({
      name: 'Review',
      trigger: { type: 'manual' },
      steps: [{ type: 'approval', name: 'Review', assignees: ['admin'], inputSchema: schema }],
    }),
  );
  expect(request.steps?.[0]?.inputSchema?.additionalData).toEqual(schema);
  expect(toPlain(draftFrom(request as WorkflowResponse)).steps?.[0]?.inputSchema).toEqual(schema);
});

it('round trips nested trigger parameters through single and multiple SDK triggers', () => {
  const parameters = {
    when: {
      all: [
        { target: 'tags', operator: 'added', term: 'Documents/Tags/Receipt' },
        { target: 'field', field: 'status', operator: 'transition', from: 'Draft', to: 'Ready' },
      ],
    },
  };
  const request = requestFrom(fromPlain({ name: 'Conditional update', trigger: { type: 'itemUpdated', parameters } }));
  expect(request.trigger?.parameters?.when?.additionalData).toEqual(parameters.when);
  expect(toPlain(draftFrom({ ...request, id: 'workflow' } as WorkflowResponse)).trigger?.parameters).toEqual(
    parameters,
  );
  const multiple = requestFrom(
    fromPlain({
      name: 'Multiple events',
      triggers: [
        { type: 'itemAdded', parameters: { when: { target: 'tags', operator: 'added' } } },
        { type: 'itemUpdated', parameters },
      ],
    }),
  );
  expect(toPlain(draftFrom({ ...multiple, id: 'workflow' } as WorkflowResponse)).triggers?.[1].parameters).toEqual(
    parameters,
  );
});
