import type { BuiltInWorkflowResponse, WorkflowResponse } from '@paperdotnet/client';
import { manualWorkflows } from './model';

export interface LaunchOption {
  id: string;
  name?: string | null;
  description?: string | null;
  inputSchema?: WorkflowResponse['inputSchema'];
  workflowId?: string;
  builtInKey?: string;
}

/** Manual library built-ins are available independently of their automatic upload setting. */
export function launchOptions(
  workflows: WorkflowResponse[],
  list?: { id?: string | null; name?: string | null },
  builtIns: BuiltInWorkflowResponse[] = [],
): LaunchOption[] {
  const manualBuiltIns = list ? builtIns.filter((builtIn) => builtIn.available && builtIn.allowManualLaunch) : [];
  const regular = manualWorkflows(workflows, list).filter(
    (workflow) =>
      !manualBuiltIns.some((builtIn) => builtIn.key === workflow.builtIn) &&
      (!list ||
        !workflow.listId ||
        !workflow.builtIn ||
        builtIns.some((b) => b.key === workflow.builtIn && b.available)),
  );
  return [
    ...regular.map((workflow) => ({
      id: workflow.id!,
      workflowId: workflow.id!,
      name: workflow.name,
      description: workflow.description,
      inputSchema:
        workflow.inputSchema ??
        (workflow.triggers ?? (workflow.trigger ? [workflow.trigger] : [])).find((t) => t.type === 'manual')?.inputs,
    })),
    ...manualBuiltIns.map((builtIn) => ({
      id: `builtin:${builtIn.key}`,
      builtInKey: builtIn.key!,
      name: builtIn.name,
      description: builtIn.description,
      inputSchema: builtIn.inputSchema,
    })),
  ];
}
