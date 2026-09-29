import { describe, expect, it } from 'vitest';
import { parameterFields, parameterInputs, parameterValues } from './built-ins';

const schema = {
  type: 'object',
  properties: {
    statusField: { type: 'string', default: 'status' },
    list: { type: 'string', description: 'The list.' },
    approvers: { type: 'array' },
    dueInHours: { type: 'number' },
    notify: { type: 'boolean' },
  },
  required: ['list', 'approvers'],
};

describe('built-in workflow parameters', () => {
  it('lists required fields first', () => {
    expect(parameterFields(schema).map((f) => f.name)).toEqual([
      'list',
      'approvers',
      'statusField',
      'dueInHours',
      'notify',
    ]);
  });

  it('starts from saved values, else defaults', () => {
    const fields = parameterFields(schema);
    expect(parameterInputs(fields, { approvers: ['alice', 'group:Finance'] })).toEqual({
      list: '',
      approvers: 'alice, group:Finance',
      statusField: 'status',
      dueInHours: '',
      notify: false,
    });
  });

  it('types values and leaves out empty ones', () => {
    const fields = parameterFields(schema);
    expect(
      parameterValues(fields, {
        list: ' Requests ',
        approvers: 'alice, , bob',
        statusField: '',
        dueInHours: '48',
        notify: true,
      }),
    ).toEqual({ list: 'Requests', approvers: ['alice', 'bob'], dueInHours: 48, notify: true });
  });
});
