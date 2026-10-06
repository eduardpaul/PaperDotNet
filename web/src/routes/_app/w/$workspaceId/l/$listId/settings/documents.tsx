import type { BuiltInWorkflowResponse, DuplicatePolicy } from '@paperdotnet/client';
import { indexRole } from '@/features/search/search-settings';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link } from '@tanstack/react-router';
import { useState, type FormEvent, type ReactNode } from 'react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { libraryWorkflowsQuery } from '@/features/documents/queries';
import { useCustomizeListWorkflow } from '@/features/workflows/customize';
import { workflowsQuery } from '@/features/workflows/queries';
import { documentSettingsQuery, useCanManageList } from '@/features/list-settings/queries';
import { listBuilder } from '@/features/lists/queries';
import { SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/documents')({ component: Documents });

interface Form {
  duplicatePolicy: DuplicatePolicy;
  ocrLanguages: string;
}

/**
 * How the library handles new files: duplicates (DOC-10), OCR languages (DOC-17), and its document workflows
 * (ADR-0038): uploads only store files; reading the text, thumbnails, pages and OCR are workflows turned on here.
 */
function Documents() {
  const { workspaceId, listId } = Route.useParams();
  return (
    <div className="flex flex-col gap-6">
      <DocumentSettings />
      <LibraryWorkflows workspaceId={workspaceId} listId={listId} />
    </div>
  );
}

function DocumentSettings() {
  const { workspaceId, listId } = Route.useParams();
  const queryClient = useQueryClient();
  const { data: settings } = useQuery(documentSettingsQuery(workspaceId, listId));
  const canManage = useCanManageList(workspaceId, listId);
  const [form, setForm] = useState<Form>();
  const [baseline, setBaseline] = useState<string>();
  if (settings && settings.odataEtag !== baseline) {
    setBaseline(settings.odataEtag ?? undefined);
    setForm({
      duplicatePolicy: settings.duplicatePolicy ?? 'warn',
      ocrLanguages: settings.ocrLanguagesInherited ? '' : (settings.ocrLanguages ?? ''),
    });
  }
  const save = useMutation({
    meta: { silent: true },
    mutationFn: async (value: Form) =>
      (await listBuilder(workspaceId, listId).documentSettings.put(
        { ...value, ocrLanguages: value.ocrLanguages.trim() || null },
        ifMatch(settings),
      ))!,
    onSuccess: (updated) => {
      queryClient.setQueryData(documentSettingsQuery(workspaceId, listId).queryKey, updated);
      toast.success('Settings saved.');
    },
  });

  if (!settings || !form) return <Skeleton className="h-64" />;
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate(form);
  };
  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Documents"
        description="What happens to files uploaded to this library."
        actions={
          <Button type="submit" variant="primary" disabled={!canManage || save.isPending}>
            Save
          </Button>
        }
      >
        <fieldset disabled={!canManage} className="divide-y">
          <SettingRow
            id="doc-duplicates"
            label="Same file again"
            hint="A file with the same content as one already in the library."
          >
            <Select
              id="doc-duplicates"
              value={form.duplicatePolicy}
              onChange={(e) => setForm({ ...form, duplicatePolicy: e.target.value as DuplicatePolicy })}
            >
              <option value="allow">Allow it</option>
              <option value="warn">Allow it, with a warning</option>
              <option value="block">Refuse it</option>
            </Select>
          </SettingRow>
          <SettingRow
            id="doc-languages"
            label="OCR languages"
            hint="For the Recognize text workflow: Tesseract codes joined with +, e.g. deu+eng. Empty uses the organization's default."
          >
            <Input
              id="doc-languages"
              placeholder={settings.ocrLanguagesInherited ? `Organization default (${settings.ocrLanguages})` : ''}
              value={form.ocrLanguages}
              onChange={(e) => setForm({ ...form, ocrLanguages: e.target.value })}
            />
          </SettingRow>
        </fieldset>
        {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}

/** The library's document workflows, each on or off here (a library without them only stores files). */
function LibraryWorkflows({ workspaceId, listId }: { workspaceId: string; listId: string }) {
  const queryClient = useQueryClient();
  const canManage = useCanManageList(workspaceId, listId);
  const { data: workflows } = useQuery(libraryWorkflowsQuery(workspaceId, listId));
  const { data: own } = useQuery(workflowsQuery(workspaceId));
  const customize = useCustomizeListWorkflow(workspaceId, listId);
  const toggle = useMutation({
    mutationFn: ({ workflow, enabled }: { workflow: BuiltInWorkflowResponse; enabled: boolean }) =>
      listBuilder(workspaceId, listId).workflows.builtIns.byKey(workflow.key!).put({ enabled }, ifMatch(workflow)),
    onSuccess: async (_, { workflow, enabled }) => {
      toast.success(`${workflow.name} is ${enabled ? 'on' : 'off'} for this library.`);
      await queryClient.invalidateQueries({ queryKey: libraryWorkflowsQuery(workspaceId, listId).queryKey });
    },
    onError: (error) => toast.error(problemMessage(error)),
  });

  if (!workflows) return <Skeleton className="h-40" />;
  return (
    <SettingsSection
      title="Workflows"
      description="Uploads only store the file. These workflows read its text, make its images and recognize scans; turn off what this library does not need."
    >
      <ul className="divide-y">
        {/* Search indexing has its own section (General settings): one pipeline of several is active. */}
        {workflows
          .filter((workflow) => workflow.role !== indexRole)
          .map((workflow) => {
            // A library's own copy that fills this workflow's role (e.g. text from an LLM) replaces it here.
            const copy = own?.find(
              (w) => w.provides === workflow.role && w.listId === listId && !w.builtIn && w.enabled,
            );
            return (
              <WorkflowSwitch
                key={`${workflow.key}-${workflow.odataEtag ?? ''}`}
                workflow={workflow}
                disabled={!canManage || !workflow.available}
                onToggle={(enabled) => toggle.mutateAsync({ workflow, enabled })}
                action={
                  copy ? (
                    <Button asChild size="sm" variant="ghost">
                      <Link
                        to="/w/$workspaceId/settings/workflows"
                        params={{ workspaceId }}
                        search={{ edit: copy.id! }}
                      >
                        Replaced by {copy.name}
                      </Link>
                    </Button>
                  ) : (
                    canManage &&
                    workflow.role &&
                    workflow.available && (
                      <Button
                        size="sm"
                        variant="ghost"
                        disabled={customize.isPending}
                        onClick={() => customize.mutate(workflow)}
                      >
                        Customize
                      </Button>
                    )
                  )
                }
              />
            );
          })}
      </ul>
    </SettingsSection>
  );
}

/** A document workflow on or off: shown at once, and back as it was when saving fails. */
function WorkflowSwitch({
  workflow,
  disabled,
  onToggle,
  action,
}: {
  workflow: BuiltInWorkflowResponse;
  disabled: boolean;
  onToggle: (enabled: boolean) => Promise<unknown>;
  action?: ReactNode;
}) {
  const [enabled, setEnabled] = useState(!!workflow.enabled);
  const [saving, setSaving] = useState(false);
  const id = `workflow-${workflow.key}`;
  return (
    <li className="flex items-start gap-3 py-3">
      <Checkbox
        id={id}
        className="mt-0.5"
        checked={enabled}
        disabled={disabled || saving}
        onChange={(e) => {
          const next = e.target.checked;
          setEnabled(next);
          setSaving(true);
          onToggle(next)
            .catch(() => setEnabled(!next))
            .finally(() => setSaving(false));
        }}
      />
      <label htmlFor={id} className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="text-[13px] font-medium">{workflow.name}</span>
        <span className="text-xs text-muted">{workflow.description}</span>
      </label>
      {action}
    </li>
  );
}
