import type { WorkflowResponse } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bot, Braces } from 'lucide-react';
import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { toast } from 'sonner';
import { listsQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import { Combobox } from '@/components/ui/combobox';
import { Alert } from '@/components/ui/feedback';
import { Input, Label, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet';
import { listQuery } from '@/features/lists/queries';
import { fieldLabel, listFields } from '@/features/lists/schema';
import { workspaceBuilder } from '@/features/workspaces/queries';
import { problemMessage } from '@/lib/errors';
import {
  approvalNames,
  describeTriggers,
  draftFrom,
  emptyWorkflow,
  fromPlain,
  requestFrom,
  toPlain,
  type WorkflowDraft,
  type PlainWorkflow,
} from './model';
import { actionCatalogQuery, workflowsQuery, triggerCatalogQuery } from './queries';
import { JsonInput, StepList } from './step-editor';

const triggerLabels: Record<string, string> = {
  manual: 'A person starts it manually',
  webhook: 'A webhook request arrives',
  itemAdded: 'An item is added',
  itemUpdated: 'An item changes',
  itemDeleted: 'An item is deleted',
  itemRestored: 'An item is restored',
  schedule: 'On a schedule',
  date: 'A date of an item is reached',
  'document.processed': 'A document is processed',
  'task.completed': 'A task is completed',
  'comment.added': 'Someone comments',
  'approval.decided': 'An approval is decided',
};

/** Creates or changes a workflow (EVT-07…09): trigger, condition and steps, or the whole thing as JSON. */
export function WorkflowEditor({
  workspaceId,
  workflow,
  canManage,
  onClose,
}: {
  workspaceId: string;
  /** Undefined for a new workflow. */
  workflow?: WorkflowResponse;
  canManage: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<WorkflowDraft>(() => (workflow ? draftFrom(workflow) : emptyWorkflow()));
  const [asJson, setAsJson] = useState(false);
  const { data: lists } = useQuery(listsQuery(workspaceId));
  const { data: triggers } = useQuery(triggerCatalogQuery);
  const { data: actions } = useQuery(actionCatalogQuery);
  // With several triggers the condition uses the fields of the first list they name.
  const listName = draft.triggers ? draft.triggers.find((t) => t.list)?.list : draft.trigger.list;
  const triggerList = lists?.find((l) => l.name === listName);
  const { data: list } = useQuery({ ...listQuery(workspaceId, triggerList?.id ?? ''), enabled: !!triggerList });
  const fields = triggerList ? listFields(list) : [];
  const setTrigger = (patch: Partial<WorkflowDraft['trigger']>) =>
    setDraft({ ...draft, trigger: { ...draft.trigger, ...patch } });

  const save = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const body = requestFrom(draft);
      const workflows = workspaceBuilder(workspaceId).workflows;
      return workflow ? workflows.byId(workflow.id!).put(body, ifMatch(workflow)) : workflows.post(body);
    },
    onSuccess: async () => {
      toast.success(workflow ? 'Workflow saved.' : 'Workflow created.');
      await queryClient.invalidateQueries({ queryKey: workflowsQuery(workspaceId).queryKey });
      onClose();
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  const context = {
    lists: lists ?? [],
    fields,
    actions: actions ?? [],
    approvals: approvalNames(draft.steps),
    disabled: !canManage,
  };

  return (
    <Sheet open onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="md:w-[min(760px,94vw)]">
        <form onSubmit={onSubmit} className="flex min-h-0 flex-1 flex-col">
          <header className="flex items-center gap-2 border-b px-5 py-3">
            <Bot className="size-5 text-accent" />
            <div className="min-w-0 flex-1">
              <SheetTitle className="truncate font-semibold">
                {workflow ? draft.name || workflow.name : 'New workflow'}
              </SheetTitle>
              <SheetDescription className="text-xs text-muted">
                {workflow ? `Version ${workflow.version}; saving creates a new version.` : 'When … then …'}
              </SheetDescription>
            </div>
            <Button type="button" size="sm" variant="ghost" aria-pressed={asJson} onClick={() => setAsJson(!asJson)}>
              <Braces /> JSON
            </Button>
            <SheetClose />
          </header>
          <div className="flex min-h-0 flex-1 flex-col gap-5 overflow-y-auto p-5">
            {asJson ? (
              <JsonDraft draft={draft} onChange={setDraft} disabled={!canManage} />
            ) : (
              <fieldset disabled={!canManage} className="contents">
                <div className="grid gap-3 sm:grid-cols-[1fr_auto]">
                  <Row label="Name">
                    {(id) => (
                      <Input
                        id={id}
                        required
                        maxLength={200}
                        value={draft.name}
                        onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                      />
                    )}
                  </Row>
                  <label className="flex items-center gap-2 self-end pb-2 text-[13px]">
                    <Checkbox
                      checked={draft.enabled}
                      onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })}
                    />
                    Enabled
                  </label>
                </div>
                <Row label="Description">
                  {(id) => (
                    <Textarea
                      id={id}
                      rows={2}
                      value={draft.description}
                      onChange={(e) => setDraft({ ...draft, description: e.target.value })}
                    />
                  )}
                </Row>

                <Row
                  label="Scope"
                  hint="Workspace workflows can react to events across any list, or run on demand. List workflows target one list."
                >
                  {(id) => (
                    <Select
                      id={id}
                      value={draft.scope ?? ''}
                      onChange={(e) =>
                        setDraft({
                          ...draft,
                          scope: e.target.value || undefined,
                          trigger:
                            e.target.value === 'workspace' && draft.trigger.type === 'manual'
                              ? { ...draft.trigger, list: '', contentType: '', terms: [] }
                              : draft.trigger,
                        })
                      }
                    >
                      <option value="">From triggers (existing behavior)</option>
                      <option value="workspace">Workspace</option>
                      <option value="list">List items</option>
                    </Select>
                  )}
                </Row>
                <Row
                  label="Input schema"
                  hint="JSON Schema for launch parameters. Use properties, required, enum and default. Add x-paperdotnet for relationship, terms, keywords or people pickers."
                >
                  {() => (
                    <JsonInput
                      aria-label="Workflow input schema"
                      rows={6}
                      value={draft.inputSchema ?? { type: 'object', properties: {} }}
                      onChange={(value) => setDraft({ ...draft, inputSchema: value as Record<string, unknown> })}
                    />
                  )}
                </Row>
                <section className="flex flex-col gap-3 rounded-lg border bg-surface-muted/30 p-4">
                  <h3 className="text-[13px] font-semibold">When</h3>
                  {draft.triggers ? (
                    <p className="text-[13px] text-muted">
                      {describeTriggers({ triggers: draft.triggers })}. This workflow has {draft.triggers.length}{' '}
                      triggers; edit them in the JSON view.
                    </p>
                  ) : (
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Row label="Trigger">
                        {(id) => (
                          <Select
                            id={id}
                            value={draft.trigger.type}
                            onChange={(e) =>
                              setTrigger({
                                type: e.target.value,
                                ...(e.target.value === 'manual' && draft.scope === 'workspace'
                                  ? { list: '', contentType: '', terms: [] }
                                  : {}),
                              })
                            }
                          >
                            {(triggers ?? []).map((t) => (
                              <option key={t.key} value={t.key!} title={t.description ?? undefined}>
                                {triggerLabels[t.key!] ?? t.key}
                              </option>
                            ))}
                            {!triggers?.some((t) => t.key === draft.trigger.type) && (
                              <option value={draft.trigger.type}>{draft.trigger.type}</option>
                            )}
                          </Select>
                        )}
                      </Row>
                      {draft.trigger.type === 'schedule' && (
                        <>
                          <Row label="Cron" hint="Minute hour day month weekday, e.g. 0 8 * * 1-5 (weekdays at 8:00).">
                            {(id) => (
                              <Input
                                id={id}
                                className="font-mono text-xs"
                                value={draft.trigger.cron}
                                onChange={(e) => setTrigger({ cron: e.target.value })}
                              />
                            )}
                          </Row>
                          <Row label="Time zone" hint="Optional, e.g. Europe/Berlin. Default: the organization's.">
                            {(id) => (
                              <Input
                                id={id}
                                value={draft.trigger.timeZone}
                                onChange={(e) => setTrigger({ timeZone: e.target.value })}
                              />
                            )}
                          </Row>
                        </>
                      )}
                      {!(draft.scope === 'workspace' && draft.trigger.type === 'manual') &&
                        draft.trigger.type !== 'schedule' &&
                        draft.trigger.type !== 'webhook' && (
                          <Row label="List">
                            {(id) => (
                              <Select
                                id={id}
                                value={draft.trigger.list}
                                onChange={(e) =>
                                  setTrigger({ list: e.target.value, contentType: '', changedFields: [] })
                                }
                              >
                                <option value="">Any list</option>
                                {lists?.map((l) => (
                                  <option key={l.id} value={l.name!}>
                                    {l.name}
                                  </option>
                                ))}
                              </Select>
                            )}
                          </Row>
                        )}
                      {draft.trigger.type === 'date' && (
                        <>
                          <Row label="Date field">
                            {(id) => (
                              <Select
                                id={id}
                                value={draft.trigger.field}
                                onChange={(e) => setTrigger({ field: e.target.value })}
                              >
                                <option value="">Choose a field</option>
                                {fields
                                  .filter((f) => f.type === 'date' || f.type === 'dateTime')
                                  .map((f) => (
                                    <option key={f.name} value={f.name!}>
                                      {fieldLabel(f)}
                                    </option>
                                  ))}
                              </Select>
                            )}
                          </Row>
                          <Row label="Hours after the date" hint="Negative for before, e.g. -24 for a day before.">
                            {(id) => (
                              <Input
                                id={id}
                                type="number"
                                value={draft.trigger.offsetHours}
                                onChange={(e) => setTrigger({ offsetHours: e.target.value })}
                              />
                            )}
                          </Row>
                        </>
                      )}
                      {triggerList && (list?.contentTypes?.length ?? 0) > 1 && (
                        <Row label="Content type">
                          {(id) => (
                            <Select
                              id={id}
                              value={draft.trigger.contentType}
                              onChange={(e) => setTrigger({ contentType: e.target.value })}
                            >
                              <option value="">Any</option>
                              {list?.contentTypes?.map((c) => (
                                <option key={c.id} value={c.name!}>
                                  {c.name}
                                </option>
                              ))}
                            </Select>
                          )}
                        </Row>
                      )}
                      {['itemAdded', 'itemUpdated'].includes(draft.trigger.type) && (
                        <Row
                          label="Trigger parameters"
                          hint="JSON conditions evaluated against the event: all/any groups with field or tags conditions. Leave empty for every event."
                        >
                          {() => (
                            <JsonInput
                              aria-label="Trigger parameters"
                              rows={8}
                              value={draft.trigger.parameters ?? {}}
                              onChange={(value) =>
                                setTrigger({
                                  parameters: Object.keys(value as Record<string, unknown>).length
                                    ? (value as { when: Record<string, unknown> })
                                    : undefined,
                                })
                              }
                            />
                          )}
                        </Row>
                      )}
                      {!triggerList && !['manual', 'schedule', 'webhook'].includes(draft.trigger.type) && (
                        <Row label="Content type" hint="Optional name or template key; leave empty for any type.">
                          {(id) => (
                            <Input
                              id={id}
                              value={draft.trigger.contentType}
                              onChange={(e) => setTrigger({ contentType: e.target.value })}
                            />
                          )}
                        </Row>
                      )}
                      {!['schedule', 'webhook', 'itemDeleted'].includes(draft.trigger.type) &&
                        !(draft.scope === 'workspace' && draft.trigger.type === 'manual') && (
                          <Row
                            label="Tags"
                            hint="Optional legacy term paths, one per line (Group/Set/Term). Matches items with any of these tags or descendants."
                          >
                            {(id) => (
                              <Textarea
                                id={id}
                                rows={3}
                                value={(draft.trigger.terms ?? []).join('\n')}
                                onChange={(e) => setTrigger({ terms: e.target.value.split('\n') })}
                              />
                            )}
                          </Row>
                        )}
                      {draft.trigger.type === 'itemUpdated' && !triggerList && (
                        <Row label="Only when these change" hint="Optional field names separated by commas.">
                          {(id) => (
                            <Input
                              id={id}
                              value={draft.trigger.changedFields.join(',')}
                              onChange={(e) => setTrigger({ changedFields: e.target.value.split(',') })}
                            />
                          )}
                        </Row>
                      )}
                      {draft.trigger.type === 'itemUpdated' && triggerList && (
                        <Row label="Only when these change">
                          {(_, labelId) => (
                            <Combobox
                              aria-labelledby={labelId}
                              multiple
                              placeholder="Any field"
                              options={fields.map((f) => ({ value: f.name!, label: fieldLabel(f) }))}
                              selected={draft.trigger.changedFields.map((name) => ({
                                value: name,
                                label: fieldLabel(fields.find((f) => f.name === name) ?? { name }),
                              }))}
                              onChange={(options) => setTrigger({ changedFields: options.map((o) => o.value) })}
                            />
                          )}
                        </Row>
                      )}
                    </div>
                  )}
                  {(draft.triggers ?? [draft.trigger]).some((t) => t.type === 'webhook') && (
                    <Row
                      label="Webhook URL"
                      hint="Send a JSON input object with an API token that has workflow.write and workspace Contribute access."
                    >
                      {(id) => (
                        <Input
                          id={id}
                          readOnly
                          value={
                            workflow?.id
                              ? `${location.origin}/v1.0/workspaces/${workspaceId}/workflows/${workflow.id}/webhook`
                              : 'Create the workflow to get its webhook URL.'
                          }
                        />
                      )}
                    </Row>
                  )}
                  <label className="flex items-center gap-2 text-[13px]">
                    <Checkbox
                      disabled={
                        (draft.triggers ?? [draft.trigger]).length === 1 &&
                        (draft.triggers ?? [draft.trigger])[0]?.type === 'manual'
                      }
                      checked={(draft.triggers ?? [draft.trigger]).some((t) => t.type === 'manual')}
                      onChange={(e) => {
                        const all = draft.triggers ?? [toPlain(draft).trigger!];
                        const next = e.target.checked
                          ? [
                              ...all,
                              { type: 'manual', list: draft.scope === 'workspace' ? null : draft.trigger.list || null },
                            ]
                          : all.filter((t) => t.type !== 'manual');
                        if (next.length) setDraft({ ...draft, triggers: next });
                      }}
                    />
                    Allow manual launch
                  </label>
                  <Row
                    label="Only if the item matches"
                    hint={
                      triggerList
                        ? "Optional. An OData filter, e.g. fields/amount gt 100 and fields/status eq 'new'."
                        : 'Choose a list to filter its items.'
                    }
                  >
                    {(id) => (
                      <Input
                        id={id}
                        className="font-mono text-xs"
                        disabled={!triggerList}
                        value={draft.condition}
                        onChange={(e) => setDraft({ ...draft, condition: e.target.value })}
                      />
                    )}
                  </Row>
                </section>

                <section className="flex flex-col gap-3">
                  <h3 className="text-[13px] font-semibold">Then</h3>
                  {draft.flow ? (
                    <p className="text-[13px] text-muted">
                      This workflow is a flow with {Object.keys(draft.flow.nodes).length} nodes. Edit it in the JSON
                      view.
                    </p>
                  ) : (
                    <StepList
                      steps={draft.steps}
                      onChange={(steps) => setDraft({ ...draft, steps })}
                      context={context}
                    />
                  )}
                </section>
              </fieldset>
            )}
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </div>
          <footer className="flex justify-end gap-2 border-t px-5 py-3">
            <Button type="button" onClick={onClose}>
              {canManage ? 'Cancel' : 'Close'}
            </Button>
            {canManage && (
              <Button type="submit" variant="primary" disabled={!draft.name.trim() || save.isPending}>
                {workflow ? 'Save' : 'Create workflow'}
              </Button>
            )}
          </footer>
        </form>
      </SheetContent>
    </Sheet>
  );
}

function Row({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: ReactNode;
  children: (id: string, labelId: string) => ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <Label htmlFor={id} id={`${id}-label`}>
        {label}
      </Label>
      {children(id, `${id}-label`)}
      {hint && <p className="text-xs text-muted">{hint}</p>}
    </div>
  );
}

/** The workflow as the API's JSON (docs/workflows.md), for what the form does not cover. */
function JsonDraft({
  draft,
  onChange,
  disabled,
}: {
  draft: WorkflowDraft;
  onChange: (draft: WorkflowDraft) => void;
  disabled: boolean;
}) {
  return (
    <div className="flex flex-col gap-2">
      <p className="text-xs text-muted">
        The workflow as the API sees it (see the workflow documentation). Changes apply when you leave the field.
      </p>
      <fieldset disabled={disabled}>
        <JsonInput
          aria-label="Workflow JSON"
          rows={24}
          value={toPlain(draft)}
          onChange={(value) => onChange(fromPlain(value as PlainWorkflow))}
        />
      </fieldset>
    </div>
  );
}
