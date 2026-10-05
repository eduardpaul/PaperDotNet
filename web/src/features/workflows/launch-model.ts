import type { BuiltInWorkflowResponse, WorkflowResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { manualWorkflows } from './model';

export interface LaunchOption {
  id: string;
  name?: string | null;
  description?: string | null;
  inputSchema?: WorkflowResponse['inputSchema'];
  workflowId?: string;
  builtInKey?: string;
  selectionMode?: string | null;
  presentation?: SelectionPresentation;
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
      presentation: selectionPresentation(
        workflow.inputSchema ??
          (workflow.triggers ?? (workflow.trigger ? [workflow.trigger] : [])).find((t) => t.type === 'manual')?.inputs,
      ),
      name: workflow.name,
      description: workflow.description,
      selectionMode: (workflow.triggers ?? (workflow.trigger ? [workflow.trigger] : [])).find(
        (t) => t.type === 'manual',
      )?.selectionMode,
      inputSchema:
        workflow.inputSchema ??
        (workflow.triggers ?? (workflow.trigger ? [workflow.trigger] : [])).find((t) => t.type === 'manual')?.inputs,
    })),
    ...manualBuiltIns.map((builtIn) => ({
      id: `builtin:${builtIn.key}`,
      builtInKey: builtIn.key!,
      presentation: selectionPresentation(builtIn.inputSchema),
      name: builtIn.name,
      description: builtIn.description,
      inputSchema: builtIn.inputSchema,
      selectionMode: builtIn.manualSelectionMode,
    })),
  ];
}

/** Declarative launch hints shared by extension and user workflows; no workflow keys are special-cased. */
export interface SelectionPresentation {
  preview?: 'image';
  primaryDescription?: string;
  itemLabel?: string;
  orderLabel?: string;
}

function selectionPresentation(schema: WorkflowResponse['inputSchema']): SelectionPresentation | undefined {
  if (!schema) return undefined;
  const hint = fieldsOf({ fields: schema })['x-paperdotnet-selection'];
  if (!hint || typeof hint !== 'object' || Array.isArray(hint)) return undefined;
  const values = hint as Record<string, unknown>;
  return {
    preview: values.preview === 'image' ? 'image' : undefined,
    primaryDescription: typeof values.primaryDescription === 'string' ? values.primaryDescription : undefined,
    itemLabel: typeof values.itemLabel === 'string' ? values.itemLabel : undefined,
    orderLabel: typeof values.orderLabel === 'string' ? values.orderLabel : undefined,
  };
}
