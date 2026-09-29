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

  it('sends steps when there is no flow', () => {
    const draft = fromPlain({ name: 'Notify', trigger: { type: 'itemAdded' }, steps: [{ type: 'delay', hours: 2 }] });
    const request = requestFrom(draft);
    expect(request.flow).toBeUndefined();
    expect(request.steps).toEqual([{ type: 'delay', hours: 2 }]);
  });
});
