// The automation editor works on a plain draft (ids for React keys, strings for number inputs). Everything converts
// through the API's JSON shape (docs/automation.md), which the JSON view also shows: steps with `inputs` as a plain
// object and `else` (the SDK names it elseEscaped because `else` is a reserved word). Unknown step types are kept.
import type { AutomationRequest, AutomationResponse, AutomationStep } from '@paperdotnet/client';
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

export interface AutomationDraft {
  name: string;
  description: string;
  enabled: boolean;
  trigger: { type: string; list: string; contentType: string; changedFields: string[] };
  condition: string;
  steps: StepDraft[];
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

export interface PlainAutomation {
  name?: string;
  description?: string | null;
  enabled?: boolean;
  trigger?: { type?: string; list?: string | null; contentType?: string | null; changedFields?: string[] | null };
  condition?: string | null;
  steps?: PlainStep[];
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

export function emptyAutomation(): AutomationDraft {
  return {
    name: '',
    description: '',
    enabled: true,
    trigger: { type: 'itemAdded', list: '', contentType: '', changedFields: [] },
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

export function fromPlain(plain: PlainAutomation): AutomationDraft {
  return {
    name: plain.name ?? '',
    description: plain.description ?? '',
    enabled: plain.enabled ?? true,
    trigger: {
      type: plain.trigger?.type ?? 'itemAdded',
      list: plain.trigger?.list ?? '',
      contentType: plain.trigger?.contentType ?? '',
      changedFields: plain.trigger?.changedFields ?? [],
    },
    condition: plain.condition ?? '',
    steps: (plain.steps ?? []).map(stepFromPlain),
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

export function toPlain(draft: AutomationDraft): PlainAutomation {
  return {
    name: draft.name.trim(),
    description: orUndefined(draft.description) ?? null,
    enabled: draft.enabled,
    trigger: {
      type: draft.trigger.type,
      list: orUndefined(draft.trigger.list) ?? null,
      contentType: orUndefined(draft.trigger.contentType) ?? null,
      changedFields:
        draft.trigger.type === 'itemUpdated' && draft.trigger.changedFields.length ? draft.trigger.changedFields : null,
    },
    condition: orUndefined(draft.condition) ?? null,
    steps: draft.steps.map(stepToPlain),
  };
}

// ---- SDK models <-> plain JSON -----------------------------------------------------------------------------------

function stepFromSdk(step: AutomationStep): PlainStep {
  const { elseEscaped, then, inputs, ...rest } = step;
  const plain = Object.fromEntries(Object.entries(rest).filter(([k, v]) => v !== null && k !== 'additionalData'));
  return {
    ...plain,
    ...(inputs ? { inputs: { ...fieldsOf({ fields: inputs }) } } : {}),
    ...(then?.length ? { then: then.map(stepFromSdk) } : {}),
    ...(elseEscaped?.length ? { else: elseEscaped.map(stepFromSdk) } : {}),
  } as PlainStep;
}

function stepToSdk(step: PlainStep): AutomationStep {
  const { else: otherwise, then, inputs, ...rest } = step;
  return {
    ...rest,
    ...(inputs ? { inputs: jsonObject(inputs) } : {}),
    ...(then ? { then: then.map(stepToSdk) } : {}),
    ...(otherwise ? { elseEscaped: otherwise.map(stepToSdk) } : {}),
  };
}

export function draftFrom(automation: AutomationResponse): AutomationDraft {
  return fromPlain({
    name: automation.name ?? '',
    description: automation.description,
    enabled: automation.enabled ?? true,
    trigger: automation.trigger ?? undefined,
    condition: automation.condition,
    steps: (automation.steps ?? []).map(stepFromSdk),
  } as PlainAutomation);
}

export function requestFrom(draft: AutomationDraft): AutomationRequest {
  const plain = toPlain(draft);
  return { ...plain, trigger: plain.trigger, steps: (plain.steps ?? []).map(stepToSdk) } as AutomationRequest;
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
  trigger: { type?: string | null; list?: string | null; contentType?: string | null } | null | undefined,
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
    default:
      return `On ${trigger?.type ?? 'an event'}${where}`;
  }
}
