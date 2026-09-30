import type { WorkflowResponse } from '@paperdotnet/client';
import { describe, expect, it } from 'vitest';
import { draftFrom, fromPlain, requestFrom, toPlain } from './model';

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
