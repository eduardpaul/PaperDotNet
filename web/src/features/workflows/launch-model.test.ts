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

  it('leaves out workflows that fill a process role, which have their own controls', () => {
    const index: BuiltInWorkflowResponse = {
      key: 'search.index',
      name: 'Index for search',
      available: true,
      enabled: true,
      allowManualLaunch: true,
      role: 'search.index',
    };
    const workflows: WorkflowResponse[] = [
      {
        id: 'copy',
        name: 'My indexing',
        provides: 'search.index',
        listId: list.id,
        enabled: true,
        trigger: { type: 'manual' },
      },
      { id: 'custom', name: 'Custom manual', enabled: true, trigger: { type: 'manual', list: list.name } },
    ];
    expect(launchOptions(workflows, list, [index, optimization]).map((option) => option.id)).toEqual([
      'custom',
      `builtin:${optimization.key}`,
    ]);
  });
});
