import type { AutomationResponse } from '@paperdotnet/client';
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
  draftFrom,
  emptyAutomation,
  fromPlain,
  requestFrom,
  toPlain,
  type AutomationDraft,
  type PlainAutomation,
} from './model';
import { actionCatalogQuery, automationsQuery, triggerCatalogQuery } from './queries';
import { JsonInput, StepList } from './step-editor';

const triggerLabels: Record<string, string> = {
  manual: 'A person starts it on an item',
  itemAdded: 'An item is added',
  itemUpdated: 'An item changes',
  itemDeleted: 'An item is deleted',
  itemRestored: 'An item is restored',
};

/** Creates or changes an automation (EVT-07…09): trigger, condition and steps, or the whole thing as JSON. */
export function AutomationEditor({
  workspaceId,
  automation,
  canManage,
  onClose,
}: {
  workspaceId: string;
  /** Undefined for a new automation. */
  automation?: AutomationResponse;
  canManage: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<AutomationDraft>(() => (automation ? draftFrom(automation) : emptyAutomation()));
  const [asJson, setAsJson] = useState(false);
  const { data: lists } = useQuery(listsQuery(workspaceId));
  const { data: triggers } = useQuery(triggerCatalogQuery);
  const { data: actions } = useQuery(actionCatalogQuery);
  const triggerList = lists?.find((l) => l.name === draft.trigger.list);
  const { data: list } = useQuery({ ...listQuery(workspaceId, triggerList?.id ?? ''), enabled: !!triggerList });
  const fields = triggerList ? listFields(list) : [];
  const setTrigger = (patch: Partial<AutomationDraft['trigger']>) =>
    setDraft({ ...draft, trigger: { ...draft.trigger, ...patch } });

  const save = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const body = requestFrom(draft);
      const automations = workspaceBuilder(workspaceId).automations;
      return automation ? automations.byId(automation.id!).put(body, ifMatch(automation)) : automations.post(body);
    },
    onSuccess: async () => {
      toast.success(automation ? 'Automation saved.' : 'Automation created.');
      await queryClient.invalidateQueries({ queryKey: automationsQuery(workspaceId).queryKey });
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
                {automation ? draft.name || automation.name : 'New automation'}
              </SheetTitle>
              <SheetDescription className="text-xs text-muted">
                {automation ? `Version ${automation.version}; saving creates a new version.` : 'When … then …'}
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

                <section className="flex flex-col gap-3 rounded-lg border bg-surface-muted/30 p-4">
                  <h3 className="text-[13px] font-semibold">When</h3>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Row label="Trigger">
                      {(id) => (
                        <Select
                          id={id}
                          value={draft.trigger.type}
                          onChange={(e) => setTrigger({ type: e.target.value })}
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
                    <Row label="List">
                      {(id) => (
                        <Select
                          id={id}
                          value={draft.trigger.list}
                          onChange={(e) => setTrigger({ list: e.target.value, contentType: '', changedFields: [] })}
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
                  <StepList steps={draft.steps} onChange={(steps) => setDraft({ ...draft, steps })} context={context} />
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
                {automation ? 'Save' : 'Create automation'}
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

/** The automation as the API's JSON (docs/automation.md), for what the form does not cover. */
function JsonDraft({
  draft,
  onChange,
  disabled,
}: {
  draft: AutomationDraft;
  onChange: (draft: AutomationDraft) => void;
  disabled: boolean;
}) {
  return (
    <div className="flex flex-col gap-2">
      <p className="text-xs text-muted">
        The automation as the API sees it (see the automation documentation). Changes apply when you leave the field.
      </p>
      <fieldset disabled={disabled}>
        <JsonInput
          aria-label="Automation JSON"
          rows={24}
          value={toPlain(draft)}
          onChange={(value) => onChange(fromPlain(value as PlainAutomation))}
        />
      </fieldset>
    </div>
  );
}
