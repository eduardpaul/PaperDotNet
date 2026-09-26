import type { CatalogEntry, ListSummary } from '@paperdotnet/client';
import { ArrowDown, ArrowUp, CheckCheck, Clock, GitBranch, Plus, Trash2, Zap } from 'lucide-react';
import { useId, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/menu';
import { Input, Label, Textarea } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import type { FieldDefinition } from '@/features/fields/values';
import { fieldLabel } from '@/features/lists/schema';
import { cn } from '@/lib/utils';
import { newStep, type StepDraft, type StepType } from './model';
import { PeopleInput } from './people-input';

export interface EditorContext {
  /** The workspace's lists (task.create picks a task list by name). */
  lists: ListSummary[];
  /** The fields of the trigger's list: item.update, conditions and field:name people. */
  fields: FieldDefinition[];
  actions: CatalogEntry[];
  /** Names of the approval steps, for conditions on their outcome. */
  approvals: string[];
  disabled?: boolean;
}

const stepMeta: Record<StepType, { label: string; icon: typeof Zap; hint: string }> = {
  action: { label: 'Do something', icon: Zap, hint: 'Update the item, file it, create a task or notify people' },
  approval: { label: 'Ask for approval', icon: CheckCheck, hint: 'Wait until someone approves or rejects' },
  condition: { label: 'If … then … else', icon: GitBranch, hint: 'Branch on an approval or the item’s values' },
  delay: { label: 'Wait', icon: Clock, hint: 'Continue after some hours' },
};

/** The steps of an automation (or of a branch), in order, with add, move and remove. */
export function StepList({
  steps,
  onChange,
  context,
  depth = 0,
}: {
  steps: StepDraft[];
  onChange: (steps: StepDraft[]) => void;
  context: EditorContext;
  depth?: number;
}) {
  const replace = (index: number, step: StepDraft) => onChange(steps.map((s, i) => (i === index ? step : s)));
  const move = (index: number, by: number) => {
    const next = [...steps];
    const [step] = next.splice(index, 1);
    next.splice(index + by, 0, step!);
    onChange(next);
  };
  return (
    <div className="flex flex-col gap-2">
      <ol className="flex flex-col gap-2">
        {steps.map((step, index) => (
          <li key={step.id}>
            <StepCard
              step={step}
              number={depth === 0 ? index + 1 : undefined}
              context={context}
              depth={depth}
              onChange={(s) => replace(index, s)}
              onRemove={() => onChange(steps.filter((_, i) => i !== index))}
              onMoveUp={index > 0 ? () => move(index, -1) : undefined}
              onMoveDown={index < steps.length - 1 ? () => move(index, 1) : undefined}
            />
          </li>
        ))}
      </ol>
      {!context.disabled && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button size="sm" variant="ghost" className="self-start text-accent">
              <Plus /> Add a step
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start" className="w-72">
            {(Object.keys(stepMeta) as StepType[])
              .filter((type) => depth < 3 || type !== 'condition')
              .map((type) => {
                const meta = stepMeta[type];
                return (
                  <DropdownMenuItem key={type} onSelect={() => onChange([...steps, newStep(type)])}>
                    <meta.icon />
                    <span>
                      <span className="block">{meta.label}</span>
                      <span className="block text-xs text-muted">{meta.hint}</span>
                    </span>
                  </DropdownMenuItem>
                );
              })}
          </DropdownMenuContent>
        </DropdownMenu>
      )}
    </div>
  );
}

function StepCard({
  step,
  number,
  context,
  depth,
  onChange,
  onRemove,
  onMoveUp,
  onMoveDown,
}: {
  step: StepDraft;
  number?: number;
  context: EditorContext;
  depth: number;
  onChange: (step: StepDraft) => void;
  onRemove: () => void;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
}) {
  const meta = stepMeta[step.type as StepType] ?? { label: step.type, icon: Zap, hint: '' };
  const set = (patch: Partial<StepDraft>) => onChange({ ...step, ...patch });
  const label = number ? `Step ${number}: ${meta.label}` : meta.label;
  return (
    <section aria-label={label} className="rounded-lg border bg-surface">
      <header className="flex items-center gap-2 border-b px-3 py-2">
        <meta.icon className="size-4 text-accent" />
        <h4 className="flex-1 text-[13px] font-medium">{label}</h4>
        {!context.disabled && (
          <div className="flex gap-0.5">
            <Button size="icon" variant="ghost" aria-label="Move up" disabled={!onMoveUp} onClick={onMoveUp}>
              <ArrowUp />
            </Button>
            <Button size="icon" variant="ghost" aria-label="Move down" disabled={!onMoveDown} onClick={onMoveDown}>
              <ArrowDown />
            </Button>
            <Button size="icon" variant="ghost" aria-label={`Remove ${label}`} onClick={onRemove}>
              <Trash2 />
            </Button>
          </div>
        )}
      </header>
      <fieldset disabled={context.disabled} className="flex flex-col gap-3 p-3">
        {step.type === 'action' && <ActionFields step={step} set={set} context={context} />}
        {step.type === 'approval' && <ApprovalFields step={step} set={set} context={context} />}
        {step.type === 'condition' && <ConditionFields step={step} set={set} context={context} depth={depth} />}
        {step.type === 'delay' && (
          <Field label="Hours">
            {(id) => (
              <Input
                id={id}
                type="number"
                min={0}
                step="any"
                className="w-32"
                value={step.hours}
                onChange={(e) => set({ hours: e.target.value })}
              />
            )}
          </Field>
        )}
      </fieldset>
    </section>
  );
}

/** A label above a control; the control gets the id (or labelId for custom controls). */
function Field({
  label,
  hint,
  className,
  children,
}: {
  label: string;
  hint?: ReactNode;
  className?: string;
  children: (id: string, labelId: string) => ReactNode;
}) {
  const id = useId();
  return (
    <div className={cn('flex min-w-0 flex-col gap-1', className)}>
      <Label htmlFor={id} id={`${id}-label`}>
        {label}
      </Label>
      {children(id, `${id}-label`)}
      {hint && <p className="text-xs text-muted">{hint}</p>}
    </div>
  );
}

const actionLabels: Record<string, string> = {
  notify: 'Notify people',
  'item.update': 'Update the item',
  'item.file': 'File the item into a folder',
  'task.create': 'Create a task',
};

const tokensHint = 'Use {title}, {fieldName}, {created:yyyy} and other tokens.';
const personFields = (fields: FieldDefinition[]) =>
  fields.filter((f) => f.type === 'person').map((f) => ({ name: f.name!, label: fieldLabel(f) }));

function ActionFields({
  step,
  set,
  context,
}: {
  step: StepDraft;
  set: (patch: Partial<StepDraft>) => void;
  context: EditorContext;
}) {
  const input = (name: string) => (step.inputs[name] as string | number | undefined) ?? '';
  const setInput = (name: string, value: unknown) => set({ inputs: { ...step.inputs, [name]: value } });
  const people = (name: string) => (Array.isArray(step.inputs[name]) ? (step.inputs[name] as string[]) : []);
  const known = ['item.update', 'item.file', 'task.create', 'notify'];
  return (
    <>
      <Field label="Action">
        {(id) => (
          <Select id={id} value={step.action} onChange={(e) => set({ action: e.target.value, inputs: {} })}>
            {context.actions.map((a) => (
              <option key={a.key} value={a.key!} title={a.description ?? undefined}>
                {actionLabels[a.key!] ?? a.key}
              </option>
            ))}
            {!context.actions.some((a) => a.key === step.action) && <option value={step.action}>{step.action}</option>}
          </Select>
        )}
      </Field>
      {step.action === 'notify' && (
        <>
          <Field label="Notify">
            {(id, labelId) => (
              <PeopleInput
                id={id}
                labelId={labelId}
                value={people('to')}
                personFields={personFields(context.fields)}
                onChange={(v) => setInput('to', v)}
              />
            )}
          </Field>
          <Field label="Title" hint={tokensHint}>
            {(id) => <Input id={id} value={input('title')} onChange={(e) => setInput('title', e.target.value)} />}
          </Field>
          <Field label="Message">
            {(id) => (
              <Textarea id={id} rows={2} value={input('body')} onChange={(e) => setInput('body', e.target.value)} />
            )}
          </Field>
        </>
      )}
      {step.action === 'item.file' && (
        <>
          <Field
            label="Folder"
            hint="Each / starts a sub-folder; missing folders are created. E.g. {created:yyyy}/{counterparty}"
          >
            {(id) => <Input id={id} value={input('folder')} onChange={(e) => setInput('folder', e.target.value)} />}
          </Field>
          <Field label="New title" hint={`Optional. ${tokensHint}`}>
            {(id) => <Input id={id} value={input('title')} onChange={(e) => setInput('title', e.target.value)} />}
          </Field>
        </>
      )}
      {step.action === 'task.create' && (
        <>
          <Field label="Task list">
            {(id) => (
              <Select id={id} value={input('list')} onChange={(e) => setInput('list', e.target.value)}>
                <option value="">Choose a task list…</option>
                {context.lists
                  .filter((l) => l.templateKey === 'tasks')
                  .map((l) => (
                    <option key={l.id} value={l.name!}>
                      {l.name}
                    </option>
                  ))}
              </Select>
            )}
          </Field>
          <Field label="Task title" hint={tokensHint}>
            {(id) => <Input id={id} value={input('title')} onChange={(e) => setInput('title', e.target.value)} />}
          </Field>
          <Field label="Assign to">
            {(id, labelId) => (
              <PeopleInput
                id={id}
                labelId={labelId}
                value={people('assignedTo')}
                personFields={personFields(context.fields)}
                onChange={(v) => setInput('assignedTo', v)}
              />
            )}
          </Field>
          <div className="flex gap-3">
            <Field label="Due in days">
              {(id) => (
                <Input
                  id={id}
                  type="number"
                  min={0}
                  className="w-28"
                  value={input('dueInDays')}
                  onChange={(e) => setInput('dueInDays', e.target.value === '' ? '' : Number(e.target.value))}
                />
              )}
            </Field>
            <Field label="Priority">
              {(id) => (
                <Select id={id} value={input('priority')} onChange={(e) => setInput('priority', e.target.value)}>
                  <option value="">Normal</option>
                  <option value="low">Low</option>
                  <option value="high">High</option>
                </Select>
              )}
            </Field>
          </div>
        </>
      )}
      {step.action === 'item.update' && (
        <FieldValues
          values={(step.inputs.fields as Record<string, unknown> | undefined) ?? {}}
          fields={context.fields}
          onChange={(values) => setInput('fields', values)}
        />
      )}
      {!known.includes(step.action) && (
        <Field label="Inputs (JSON)" hint="The inputs this action takes; see its extension's documentation.">
          {(id) => (
            <JsonInput
              id={id}
              value={step.inputs}
              onChange={(inputs) => set({ inputs: inputs as Record<string, unknown> })}
            />
          )}
        </Field>
      )}
    </>
  );
}

/** item.update: field → value pairs; values are text with tokens (or JSON for numbers, lists and booleans). */
function FieldValues({
  values,
  fields,
  onChange,
}: {
  values: Record<string, unknown>;
  fields: FieldDefinition[];
  onChange: (values: Record<string, unknown>) => void;
}) {
  const entries = Object.entries(values);
  const unused = fields.filter((f) => f.name && !(f.name in values));
  return (
    <div className="flex flex-col gap-2">
      <p className="text-[13px] font-medium">Set these fields</p>
      {entries.map(([name, value]) => {
        const field = fields.find((f) => f.name === name);
        return (
          <div key={name} className="flex items-center gap-2">
            <span className="w-36 truncate text-[13px]">{field ? fieldLabel(field) : name}</span>
            <Input
              aria-label={`New value of ${field ? fieldLabel(field) : name}`}
              value={typeof value === 'string' ? value : JSON.stringify(value)}
              onChange={(e) => onChange({ ...values, [name]: parseValue(e.target.value, field) })}
            />
            <Button
              size="icon"
              variant="ghost"
              aria-label={`Do not set ${name}`}
              onClick={() => onChange(Object.fromEntries(entries.filter(([n]) => n !== name)))}
            >
              <Trash2 />
            </Button>
          </div>
        );
      })}
      {unused.length > 0 && (
        <Select
          aria-label="Add a field to set"
          className="w-60"
          value=""
          onChange={(e) => e.target.value && onChange({ ...values, [e.target.value]: '' })}
        >
          <option value="">Add a field…</option>
          {unused.map((f) => (
            <option key={f.name} value={f.name!}>
              {fieldLabel(f)}
            </option>
          ))}
        </Select>
      )}
      {!fields.length && <p className="text-xs text-muted">Choose the trigger’s list first to pick its fields.</p>}
    </div>
  );
}

/** Text stays text (tokens work there); numbers, booleans and lists are sent as JSON when they parse. */
function parseValue(text: string, field: FieldDefinition | undefined): unknown {
  if (!field || ['text', 'note', 'choice', 'email', 'url', 'date', 'dateTime'].includes(field.type ?? '')) return text;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function ApprovalFields({
  step,
  set,
  context,
}: {
  step: StepDraft;
  set: (patch: Partial<StepDraft>) => void;
  context: EditorContext;
}) {
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Name" hint="Later steps refer to the outcome by this name.">
          {(id) => (
            <Input
              id={id}
              required
              placeholder="e.g. Manager"
              value={step.name}
              onChange={(e) => set({ name: e.target.value })}
            />
          )}
        </Field>
        <Field label="Request title" hint={tokensHint}>
          {(id) => (
            <Input
              id={id}
              placeholder="Approve {title}"
              value={step.title}
              onChange={(e) => set({ title: e.target.value })}
            />
          )}
        </Field>
      </div>
      <Field label="Approvers" hint="The first decision counts.">
        {(id, labelId) => (
          <PeopleInput
            id={id}
            labelId={labelId}
            value={step.assignees}
            personFields={personFields(context.fields)}
            onChange={(assignees) => set({ assignees })}
          />
        )}
      </Field>
      <div className="grid gap-3 sm:grid-cols-[8rem_1fr]">
        <Field label="Due in hours">
          {(id) => (
            <Input
              id={id}
              type="number"
              min={1}
              value={step.dueInHours}
              onChange={(e) => set({ dueInHours: e.target.value })}
            />
          )}
        </Field>
        <Field label="Then also ask" hint="Added when the request is overdue.">
          {(id, labelId) => (
            <PeopleInput
              id={id}
              labelId={labelId}
              value={step.escalateTo}
              personFields={personFields(context.fields)}
              onChange={(escalateTo) => set({ escalateTo })}
            />
          )}
        </Field>
      </div>
    </>
  );
}

function ConditionFields({
  step,
  set,
  context,
  depth,
}: {
  step: StepDraft;
  set: (patch: Partial<StepDraft>) => void;
  context: EditorContext;
  depth: number;
}) {
  const byFilter = step.filter.trim() !== '' || (!context.approvals.length && !step.step);
  return (
    <>
      <div className="flex flex-wrap items-end gap-2">
        <Field label="If">
          {(id) => (
            <Select
              id={id}
              className="w-48"
              value={byFilter ? 'filter' : 'outcome'}
              onChange={(e) =>
                set(
                  e.target.value === 'filter'
                    ? { filter: step.filter || "fields/status eq 'done'", step: '' }
                    : { filter: '', step: context.approvals[0] ?? '' },
                )
              }
            >
              <option value="outcome" disabled={!context.approvals.length}>
                An approval’s outcome
              </option>
              <option value="filter">The item matches</option>
            </Select>
          )}
        </Field>
        {byFilter ? (
          <Field label="Filter (OData)" className="min-w-60 flex-1">
            {(id) => (
              <Input
                id={id}
                className="font-mono text-xs"
                placeholder="fields/amount gt 100"
                value={step.filter}
                onChange={(e) => set({ filter: e.target.value })}
              />
            )}
          </Field>
        ) : (
          <>
            <Field label="Approval">
              {(id) => (
                <Select id={id} className="w-44" value={step.step} onChange={(e) => set({ step: e.target.value })}>
                  {[...new Set([...context.approvals, step.step].filter(Boolean))].map((name) => (
                    <option key={name} value={name}>
                      {name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field label="Was">
              {(id) => (
                <Select id={id} className="w-36" value={step.is} onChange={(e) => set({ is: e.target.value })}>
                  <option value="approved">approved</option>
                  <option value="rejected">rejected</option>
                </Select>
              )}
            </Field>
          </>
        )}
      </div>
      <Branch label="Then">
        <StepList steps={step.then} onChange={(then) => set({ then })} context={context} depth={depth + 1} />
      </Branch>
      <Branch label="Otherwise">
        <StepList steps={step.else} onChange={(steps) => set({ else: steps })} context={context} depth={depth + 1} />
      </Branch>
    </>
  );
}

function Branch({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="border-l-2 border-accent/30 pl-3">
      <p className="mb-1.5 text-xs font-semibold tracking-wide text-muted uppercase">{label}</p>
      {children}
    </div>
  );
}

/** Raw JSON with a parse check (extension inputs, advanced editing). */
export function JsonInput({
  id,
  value,
  onChange,
  rows = 4,
  'aria-label': ariaLabel,
}: {
  id?: string;
  value: unknown;
  onChange: (value: unknown) => void;
  rows?: number;
  'aria-label'?: string;
}) {
  return (
    <Textarea
      id={id}
      aria-label={ariaLabel}
      rows={rows}
      className="font-mono text-xs"
      defaultValue={JSON.stringify(value ?? {}, null, 2)}
      onBlur={(e) => {
        try {
          onChange(JSON.parse(e.target.value || '{}'));
          e.target.setCustomValidity('');
        } catch {
          e.target.setCustomValidity('This is not valid JSON.');
          e.target.reportValidity();
        }
      }}
    />
  );
}
