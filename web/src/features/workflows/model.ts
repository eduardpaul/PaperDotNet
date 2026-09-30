// The workflow editor works on a plain draft (ids for React keys, strings for number inputs). Everything converts
// through the API's JSON shape (docs/workflows.md), which the JSON view also shows: steps with `inputs` as a plain
// object and `else` (the SDK names it elseEscaped because `else` is a reserved word). Unknown step types are kept, and so
// are a `flow` and `variables` (ADR-0036), which the form does not edit: flows are edited in the JSON view.
import type { WorkflowRequest, WorkflowResponse, WorkflowStep } from '@paperdotnet/client';
import { fields as jsonObject, fieldsOf } from '@paperdotnet/client';

export type StepType = 'action' | 'approval' | 'condition' | 'delay';

export interface StepDraft {
  id: string;
  type: StepType | string;
  // action
  action: string;
  inputs: Record<string, unknown>;
  // approval
  name: string;
  assignees: string[];
  title: string;
  dueInHours: string;
  escalateTo: string[];
  // condition: an approval outcome (step + is) or an OData filter
  step: string;
  is: string;
  filter: string;
  then: StepDraft[];
  else: StepDraft[];
  // delay
  hours: string;
}

export interface WorkflowDraft {
  name: string;
  description: string;
  enabled: boolean;
  trigger: TriggerDraft;
  condition: string;
  steps: StepDraft[];
  /** A flow (nodes connected by outcome ports) instead of steps; kept as the API's JSON. */
  flow?: PlainFlow;
  variables?: Record<string, unknown>;
  /** Runs on the same item: parallel (default), skip or replace; kept as the API has it. */
  concurrency?: string;
}

/** The trigger in the form: text inputs as strings; terms and manual inputs are kept as the API's JSON. */
export interface TriggerDraft {
  type: string;
  list: string;
  contentType: string;
  changedFields: string[];
  /** schedule: 5-field cron and an optional IANA time zone. */
  cron: string;
  timeZone: string;
  /** date: the date field and the offset in hours (negative: before). */
  field: string;
  offsetHours: string;
  terms?: string[];
  inputs?: Record<string, unknown>;
}

/** A trigger as the API's JSON has it. */
export interface PlainTrigger {
  type?: string;
  list?: string | null;
  contentType?: string | null;
  changedFields?: string[] | null;
  terms?: string[] | null;
  cron?: string | null;
  timeZone?: string | null;
  field?: string | null;
  offsetHours?: number | null;
  inputs?: Record<string, unknown> | null;
}

/** A flow as the API's JSON has it (docs/workflows.md). */
export interface PlainFlow {
  start: string;
  nodes: Record<string, unknown>;
}

/** A step as the API's JSON has it. */
export type PlainStep = {
  type?: string;
  name?: string;
  action?: string;
  inputs?: Record<string, unknown>;
  assignees?: string[];
  title?: string;
  dueInHours?: number;
  escalateTo?: string[];
  step?: string;
  is?: string;
  filter?: string;
  then?: PlainStep[];
  else?: PlainStep[];
  hours?: number;
};

export interface PlainWorkflow {
  name?: string;
  description?: string | null;
  enabled?: boolean;
  trigger?: PlainTrigger;
  condition?: string | null;
  steps?: PlainStep[];
  flow?: PlainFlow;
  variables?: Record<string, unknown>;
  concurrency?: string | null;
}

let counter = 0;
/** A local key for list rendering (never sent). */
export const newId = () => `s${++counter}`;

export function newStep(type: StepType): StepDraft {
  return {
    id: newId(),
    type,
    action: type === 'action' ? 'notify' : '',
    inputs: {},
    name: '',
    assignees: [],
    title: '',
    dueInHours: '',
    escalateTo: [],
    step: '',
    is: 'approved',
    filter: '',
    then: [],
    else: [],
    hours: type === 'delay' ? '24' : '',
  };
}

export function emptyWorkflow(): WorkflowDraft {
  return {
    name: '',
    description: '',
    enabled: true,
    trigger: {
      type: 'itemAdded',
      list: '',
      contentType: '',
      changedFields: [],
      cron: '',
      timeZone: '',
      field: '',
      offsetHours: '',
    },
    condition: '',
    steps: [newStep('action')],
  };
}

// ---- Plain JSON <-> draft ----------------------------------------------------------------------------------------

const text = (value: number | null | undefined) => (value === null || value === undefined ? '' : String(value));
const number = (value: string) => (value.trim() === '' ? undefined : Number(value));
const orUndefined = (value: string) => value.trim() || undefined;

function stepFromPlain(step: PlainStep): StepDraft {
  return {
    ...newStep('action'),
    type: step.type ?? 'action',
    action: step.action ?? '',
    inputs: { ...(step.inputs ?? {}) },
    name: step.name ?? '',
    assignees: step.assignees ?? [],
    title: step.title ?? '',
    dueInHours: text(step.dueInHours),
    escalateTo: step.escalateTo ?? [],
    step: step.step ?? '',
    is: step.is ?? 'approved',
    filter: step.filter ?? '',
    then: (step.then ?? []).map(stepFromPlain),
    else: (step.else ?? []).map(stepFromPlain),
    hours: text(step.hours),
  };
}

export function fromPlain(plain: PlainWorkflow): WorkflowDraft {
  return {
    name: plain.name ?? '',
    description: plain.description ?? '',
    enabled: plain.enabled ?? true,
    trigger: {
      type: plain.trigger?.type ?? 'itemAdded',
      list: plain.trigger?.list ?? '',
      contentType: plain.trigger?.contentType ?? '',
      changedFields: plain.trigger?.changedFields ?? [],
      cron: plain.trigger?.cron ?? '',
      timeZone: plain.trigger?.timeZone ?? '',
      field: plain.trigger?.field ?? '',
      offsetHours: text(plain.trigger?.offsetHours),
      ...(plain.trigger?.terms?.length ? { terms: plain.trigger.terms } : {}),
      ...(plain.trigger?.inputs ? { inputs: plain.trigger.inputs } : {}),
    },
    condition: plain.condition ?? '',
    steps: (plain.steps ?? []).map(stepFromPlain),
    ...(plain.flow ? { flow: plain.flow } : {}),
    ...(plain.variables ? { variables: plain.variables } : {}),
    ...(plain.concurrency ? { concurrency: plain.concurrency } : {}),
  };
}

function stepToPlain(step: StepDraft): PlainStep {
  switch (step.type) {
    case 'approval':
      return {
        type: 'approval',
        name: step.name.trim(),
        assignees: step.assignees,
        title: orUndefined(step.title),
        dueInHours: number(step.dueInHours),
        escalateTo: step.escalateTo.length ? step.escalateTo : undefined,
      };
    case 'condition':
      return {
        type: 'condition',
        ...(step.filter.trim() ? { filter: step.filter.trim() } : { step: step.step, is: step.is }),
        then: step.then.map(stepToPlain),
        else: step.else.map(stepToPlain),
      };
    case 'delay':
      return { type: 'delay', hours: number(step.hours) };
    default:
      return {
        type: step.type,
        name: orUndefined(step.name),
        action: step.action,
        inputs: Object.fromEntries(Object.entries(step.inputs).filter(([, v]) => v !== '' && v !== undefined)),
      };
  }
}

export function toPlain(draft: WorkflowDraft): PlainWorkflow {
  return {
    name: draft.name.trim(),
    description: orUndefined(draft.description) ?? null,
    enabled: draft.enabled,
    trigger: triggerToPlain(draft.trigger),
    condition: orUndefined(draft.condition) ?? null,
    ...(draft.flow ? { flow: draft.flow } : { steps: draft.steps.map(stepToPlain) }),
    ...(draft.variables ? { variables: draft.variables } : {}),
    ...(draft.concurrency ? { concurrency: draft.concurrency } : {}),
  };
}

/** Only the settings the trigger type uses (the API rejects the others). */
function triggerToPlain(trigger: TriggerDraft): PlainTrigger {
  const { type } = trigger;
  return {
    type,
    list: type === 'schedule' ? null : (orUndefined(trigger.list) ?? null),
    contentType: type === 'schedule' ? null : (orUndefined(trigger.contentType) ?? null),
    changedFields: type === 'itemUpdated' && trigger.changedFields.length ? trigger.changedFields : null,
    ...(type === 'schedule' ? { cron: trigger.cron.trim(), timeZone: orUndefined(trigger.timeZone) ?? null } : {}),
    ...(type === 'date'
      ? { field: orUndefined(trigger.field) ?? null, offsetHours: number(trigger.offsetHours) ?? null }
      : {}),
    ...(trigger.terms?.length && type !== 'schedule' ? { terms: trigger.terms } : {}),
    ...(trigger.inputs && type === 'manual' ? { inputs: trigger.inputs } : {}),
  };
}

// ---- SDK models <-> plain JSON -----------------------------------------------------------------------------------

function stepFromSdk(step: WorkflowStep): PlainStep {
  const { elseEscaped, then, inputs, ...rest } = step;
  const plain = Object.fromEntries(Object.entries(rest).filter(([k, v]) => v !== null && k !== 'additionalData'));
  return {
    ...plain,
    ...(inputs ? { inputs: { ...fieldsOf({ fields: inputs }) } } : {}),
    ...(then?.length ? { then: then.map(stepFromSdk) } : {}),
    ...(elseEscaped?.length ? { else: elseEscaped.map(stepFromSdk) } : {}),
  } as PlainStep;
}

function stepToSdk(step: PlainStep): WorkflowStep {
  const { else: otherwise, then, inputs, ...rest } = step;
  return {
    ...rest,
    ...(inputs ? { inputs: jsonObject(inputs) } : {}),
    ...(then ? { then: then.map(stepToSdk) } : {}),
    ...(otherwise ? { elseEscaped: otherwise.map(stepToSdk) } : {}),
  };
}

export function draftFrom(workflow: WorkflowResponse): WorkflowDraft {
  return fromPlain({
    name: workflow.name ?? '',
    description: workflow.description,
    enabled: workflow.enabled ?? true,
    trigger: workflow.trigger
      ? {
          ...workflow.trigger,
          inputs: workflow.trigger.inputs ? { ...fieldsOf({ fields: workflow.trigger.inputs }) } : undefined,
        }
      : undefined,
    condition: workflow.condition,
    steps: (workflow.steps ?? []).map(stepFromSdk),
    ...(workflow.flow
      ? { flow: { start: workflow.flow.start ?? '', nodes: { ...(workflow.flow.nodes?.additionalData ?? {}) } } }
      : {}),
    ...(workflow.variables ? { variables: { ...fieldsOf({ fields: workflow.variables }) } } : {}),
    concurrency: workflow.concurrency,
  } as PlainWorkflow);
}

export function requestFrom(draft: WorkflowDraft): WorkflowRequest {
  const { flow, variables, steps, trigger, ...plain } = toPlain(draft);
  const { inputs, ...triggerRest } = trigger ?? {};
  return {
    ...plain,
    trigger: { ...triggerRest, ...(inputs ? { inputs: jsonObject(inputs) } : {}) },
    ...(flow
      ? { flow: { start: flow.start, nodes: { additionalData: flow.nodes } } }
      : { steps: (steps ?? []).map(stepToSdk) }),
    ...(variables ? { variables: jsonObject(variables) } : {}),
  } as WorkflowRequest;
}

// ---- Helpers for the editor and lists ---------------------------------------------------------------------------

/** Names of the approval steps, for "if the approval … was …". */
export function approvalNames(steps: StepDraft[]): string[] {
  return steps.flatMap((s) => [
    ...(s.type === 'approval' && s.name.trim() ? [s.name.trim()] : []),
    ...approvalNames(s.then),
    ...approvalNames(s.else),
  ]);
}

/** A short sentence for lists: "When an item is added in Invoices". */
export function describeTrigger(
  trigger:
    | {
        type?: string | null;
        list?: string | null;
        contentType?: string | null;
        cron?: string | null;
        field?: string | null;
      }
    | null
    | undefined,
): string {
  const where = trigger?.list ? ` in ${trigger.list}` : '';
  const what = trigger?.contentType ? ` (${trigger.contentType})` : '';
  switch (trigger?.type) {
    case 'manual':
      return `Started by a person on an item${where}`;
    case 'itemAdded':
      return `When an item is added${where}${what}`;
    case 'itemUpdated':
      return `When an item changes${where}${what}`;
    case 'itemDeleted':
      return `When an item is deleted${where}${what}`;
    case 'itemRestored':
      return `When an item is restored${where}${what}`;
    case 'schedule':
      return `On the schedule ${trigger?.cron ?? ''}`.trim();
    case 'date':
      return `When ${trigger?.field ?? 'a date'} is reached${where}`;
    case 'document.processed':
      return `When a document is processed${where}`;
    case 'task.completed':
      return `When a task is completed${where}`;
    case 'comment.added':
      return `When someone comments${where}`;
    case 'approval.decided':
      return 'When an approval is decided';
    default:
      return `On ${trigger?.type ?? 'an event'}${where}`;
  }
}
