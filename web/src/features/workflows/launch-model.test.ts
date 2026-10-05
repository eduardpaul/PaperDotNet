import type { BuiltInWorkflowResponse, WorkflowResponse } from '@paperdotnet/client';
import { describe, expect, it } from 'vitest';
import { launchOptions } from './launch-model';

const list = { id: 'photos', name: 'Photos' };
const optimization: BuiltInWorkflowResponse = {
  key: 'paperdotnet.storageoptimization.optimize',
  name: 'Optimize document storage',
  available: true,
  enabled: false,
  allowManualLaunch: true,
};

describe('manual launch options', () => {
  it('reads declarative selection presentation for arbitrary extension and user workflows', () => {
    const inputSchema = {
      additionalData: {
        type: 'object',
        properties: {},
        'x-paperdotnet-selection': {
          preview: 'image',
          itemLabel: 'page',
          orderLabel: 'Page order',
          primaryDescription: 'Keep this item.',
        },
      },
    };
    const options = launchOptions(
      [
        {
          id: 'custom',
          enabled: true,
          trigger: { type: 'manual', list: list.name, selectionMode: 'selection' },
          inputSchema,
        },
      ],
      list,
      [{ ...optimization, key: 'acme.documents.combine', inputSchema, manualSelectionMode: 'selection' }],
    );
    expect(options).toHaveLength(2);
    for (const option of options)
      expect(option.presentation).toEqual({
        preview: 'image',
        itemLabel: 'page',
        orderLabel: 'Page order',
        primaryDescription: 'Keep this item.',
      });
  });
  it('offers an available manual built-in before it has ever been enabled', () => {
    expect(launchOptions([], list, [optimization])).toEqual([
      expect.objectContaining({ builtInKey: optimization.key, name: optimization.name }),
    ]);
    expect(launchOptions([], undefined, [optimization])).toEqual([]);
  });

  it('offers one optimization option when automatic runs are enabled, and keeps ordinary disabled workflows hidden', () => {
    const workflows: WorkflowResponse[] = [
      {
        id: 'optimization',
        builtIn: optimization.key,
        listId: list.id,
        enabled: true,
        trigger: { type: 'manual', list: list.name },
      },
      { id: 'disabled', enabled: false, trigger: { type: 'manual' } },
      { id: 'custom', name: 'Custom manual', enabled: true, trigger: { type: 'manual', list: list.name } },
    ];
    const options = launchOptions(workflows, list, [{ ...optimization, enabled: true }]);
    expect(options.map((option) => option.id)).toEqual(['custom', `builtin:${optimization.key}`]);
    expect(launchOptions(workflows, list, [])).toEqual([expect.objectContaining({ workflowId: 'custom' })]);
  });

  it('requires availability and manual opt-in, retaining a manual launch form', () => {
    expect(launchOptions([], list, [{ ...optimization, available: false }])).toEqual([]);
    expect(launchOptions([], list, [{ ...optimization, allowManualLaunch: false }])).toEqual([]);
    const inputSchema = { additionalData: { type: 'object', properties: { note: { type: 'string' } } } };
    expect(launchOptions([], list, [{ ...optimization, inputSchema }])[0]?.inputSchema).toEqual(inputSchema);
  });
});
