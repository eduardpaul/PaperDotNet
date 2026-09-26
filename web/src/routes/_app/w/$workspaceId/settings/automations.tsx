import type { AutomationResponse } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { Bot, History, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Checkbox } from '@/components/ui/select';
import { AutomationEditor } from '@/features/automations/automation-editor';
import { describeTrigger, draftFrom, requestFrom } from '@/features/automations/model';
import { automationsQuery } from '@/features/automations/queries';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { workspaceBuilder, workspaceQuery } from '@/features/workspaces/queries';
import { useFormat } from '@/lib/preferences';

interface AutomationsSearch {
  /** The automation in the editor: its id, or "new". */
  edit?: string;
}

export const Route = createFileRoute('/_app/w/$workspaceId/settings/automations')({
  validateSearch: (search: Record<string, unknown>): AutomationsSearch => ({
    edit: typeof search.edit === 'string' ? search.edit : undefined,
  }),
  component: Automations,
});

/** The workspace's automations (EVT-07…09): what triggers them, on or off, and an editor. */
function Automations() {
  const { workspaceId } = Route.useParams();
  const { edit } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const format = useFormat();
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const { data: automations, isPending } = useQuery(automationsQuery(workspaceId));
  const [deleting, setDeleting] = useState<AutomationResponse>();
  const canManage = workspace?.access === 'manage';
  const setEdit = (value?: string) => void navigate({ search: { edit: value } });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: automationsQuery(workspaceId).queryKey });

  const toggle = useMutation({
    mutationFn: ({ automation, enabled }: { automation: AutomationResponse; enabled: boolean }) =>
      workspaceBuilder(workspaceId)
        .automations.byId(automation.id!)
        .put({ ...requestFrom(draftFrom(automation)), enabled }, ifMatch(automation)),
    onSettled: invalidate,
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (automation: AutomationResponse) =>
      workspaceBuilder(workspaceId).automations.byId(automation.id!).delete(ifMatch(automation)),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('Automation deleted.');
      await invalidate();
    },
  });
  const editing = edit === 'new' ? undefined : automations?.find((a) => a.id === edit);

  return (
    <>
      <SettingsSection
        title="Automations"
        description="React to changes in this workspace: file documents, create tasks, ask for approvals, notify people."
        className="px-0 pb-0"
        actions={
          canManage && (
            <Button variant="primary" onClick={() => setEdit('new')}>
              <Plus /> New automation
            </Button>
          )
        }
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-16" />
        ) : automations?.length ? (
          <ul className="divide-y border-t">
            {automations.map((automation) => (
              <li key={automation.id} className="flex items-center gap-3 px-5 py-3">
                <EnabledToggle
                  automation={automation}
                  disabled={!canManage}
                  onToggle={(enabled) => toggle.mutateAsync({ automation, enabled })}
                />
                <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setEdit(automation.id!)}>
                  <span className="flex items-center gap-2">
                    <span className="truncate text-[13px] font-medium">{automation.name}</span>
                    {!automation.enabled && <Badge>Off</Badge>}
                  </span>
                  <span className="block truncate text-xs text-muted">
                    {describeTrigger(automation.trigger)} · {automation.steps?.length ?? 0}{' '}
                    {automation.steps?.length === 1 ? 'step' : 'steps'} · v{automation.version}, changed{' '}
                    {format.relative(automation.updatedAt)}
                  </span>
                </button>
                <Button asChild variant="ghost" size="icon" aria-label={`Runs of ${automation.name}`}>
                  <Link
                    to="/w/$workspaceId/settings/runs"
                    params={{ workspaceId }}
                    search={{ automation: automation.id! }}
                  >
                    <History />
                  </Link>
                </Button>
                {canManage && (
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label={`Delete ${automation.name}`}
                    onClick={() => setDeleting(automation)}
                  >
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState icon={Bot} title="No automations yet" className="border-t py-8">
            For example: when an invoice arrives, file it by year and ask a manager to approve it.
          </EmptyState>
        )}
      </SettingsSection>
      {edit && (edit === 'new' || editing) && (
        <AutomationEditor
          key={edit}
          workspaceId={workspaceId}
          automation={editing}
          canManage={canManage}
          onClose={() => setEdit(undefined)}
        />
      )}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(undefined)}
        title={`Delete “${deleting?.name ?? ''}”?`}
        description="Its run history is deleted too. While runs are still going (or waiting for an approval), cancel them first or turn the automation off."
        confirm="Delete"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  );
}

/** On/off at once (local state keeps the click); the server's value wins again when the save fails. */
function EnabledToggle({
  automation,
  disabled,
  onToggle,
}: {
  automation: AutomationResponse;
  disabled: boolean;
  onToggle: (enabled: boolean) => Promise<unknown>;
}) {
  const [enabled, setEnabled] = useState(!!automation.enabled);
  const [baseline, setBaseline] = useState(automation.odataEtag);
  if (automation.odataEtag !== baseline) {
    setBaseline(automation.odataEtag);
    setEnabled(!!automation.enabled);
  }
  return (
    <Checkbox
      aria-label={`${automation.name} enabled`}
      checked={enabled}
      disabled={disabled}
      onChange={(e) => {
        const next = e.target.checked;
        setEnabled(next);
        onToggle(next).catch(() => setEnabled(!next));
      }}
    />
  );
}
