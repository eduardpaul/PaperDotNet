import type { WorkflowResponse } from '@paperdotnet/client';
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
import { BuiltInWorkflows } from '@/features/workflows/built-in-workflows';
import { WorkflowEditor } from '@/features/workflows/workflow-editor';
import { describeTrigger, draftFrom, requestFrom } from '@/features/workflows/model';
import { builtInWorkflowsQuery, workflowsQuery } from '@/features/workflows/queries';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { workspaceBuilder, workspaceQuery } from '@/features/workspaces/queries';
import { useFormat } from '@/lib/preferences';

interface WorkflowsSearch {
  /** The workflow in the editor: its id, or "new". */
  edit?: string;
}

export const Route = createFileRoute('/_app/w/$workspaceId/settings/workflows')({
  validateSearch: (search: Record<string, unknown>): WorkflowsSearch => ({
    edit: typeof search.edit === 'string' ? search.edit : undefined,
  }),
  component: Workflows,
});

/** The workspace's workflows (EVT-07…09): what triggers them, on or off, and an editor. */
function Workflows() {
  const { workspaceId } = Route.useParams();
  const { edit } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const format = useFormat();
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const { data: workflows, isPending } = useQuery(workflowsQuery(workspaceId));
  const [deleting, setDeleting] = useState<WorkflowResponse>();
  const canManage = workspace?.access === 'manage';
  const setEdit = (value?: string) => void navigate({ search: { edit: value } });
  const invalidate = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: workflowsQuery(workspaceId).queryKey }),
      queryClient.invalidateQueries({ queryKey: builtInWorkflowsQuery(workspaceId).queryKey }),
    ]);

  // A built-in workflow is switched through its own settings (its definition cannot be replaced).
  const toggle = useMutation({
    mutationFn: ({ workflow, enabled }: { workflow: WorkflowResponse; enabled: boolean }) =>
      workflow.builtIn
        ? workspaceBuilder(workspaceId).workflows.builtIns.byKey(workflow.builtIn).put({ enabled })
        : workspaceBuilder(workspaceId)
            .workflows.byId(workflow.id!)
            .put({ ...requestFrom(draftFrom(workflow)), enabled }, ifMatch(workflow)),
    onSettled: invalidate,
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (workflow: WorkflowResponse) =>
      workspaceBuilder(workspaceId).workflows.byId(workflow.id!).delete(ifMatch(workflow)),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('Workflow deleted.');
      await invalidate();
    },
  });
  const editing = edit === 'new' ? undefined : workflows?.find((a) => a.id === edit);

  return (
    <>
      <SettingsSection
        title="Workflows"
        description="React to changes in this workspace: file documents, create tasks, ask for approvals, notify people."
        className="px-0 pb-0"
        actions={
          canManage && (
            <Button variant="primary" onClick={() => setEdit('new')}>
              <Plus /> New workflow
            </Button>
          )
        }
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-16" />
        ) : workflows?.length ? (
          <ul className="divide-y border-t">
            {workflows.map((workflow) => (
              <li key={workflow.id} className="flex items-center gap-3 px-5 py-3">
                <EnabledToggle
                  workflow={workflow}
                  disabled={!canManage}
                  onToggle={(enabled) => toggle.mutateAsync({ workflow, enabled })}
                />
                <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setEdit(workflow.id!)}>
                  <span className="flex items-center gap-2">
                    <span className="truncate text-[13px] font-medium">{workflow.name}</span>
                    {workflow.builtIn && <Badge tone="accent">Built-in</Badge>}
                    {!workflow.enabled && <Badge>Off</Badge>}
                  </span>
                  <span className="block truncate text-xs text-muted">
                    {describeTrigger(workflow.trigger)} · {workflow.steps?.length ?? 0}{' '}
                    {workflow.steps?.length === 1 ? 'step' : 'steps'} · v{workflow.version}, changed{' '}
                    {format.relative(workflow.updatedAt)}
                  </span>
                </button>
                <Button asChild variant="ghost" size="icon" aria-label={`Runs of ${workflow.name}`}>
                  <Link to="/w/$workspaceId/settings/runs" params={{ workspaceId }} search={{ workflow: workflow.id! }}>
                    <History />
                  </Link>
                </Button>
                {canManage && (
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label={`Delete ${workflow.name}`}
                    onClick={() => setDeleting(workflow)}
                  >
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState icon={Bot} title="No workflows yet" className="border-t py-8">
            For example: when an invoice arrives, file it by year and ask a manager to approve it.
          </EmptyState>
        )}
      </SettingsSection>
      <BuiltInWorkflows workspaceId={workspaceId} canManage={canManage} onCopied={(id) => setEdit(id)} />
      {edit && (edit === 'new' || editing) && (
        <WorkflowEditor
          key={edit}
          workspaceId={workspaceId}
          workflow={editing}
          canManage={canManage && !editing?.builtIn}
          onClose={() => setEdit(undefined)}
        />
      )}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(undefined)}
        title={`Delete “${deleting?.name ?? ''}”?`}
        description="Its run history is deleted too. While runs are still going (or waiting for an approval), cancel them first or turn the workflow off."
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
  workflow,
  disabled,
  onToggle,
}: {
  workflow: WorkflowResponse;
  disabled: boolean;
  onToggle: (enabled: boolean) => Promise<unknown>;
}) {
  const [enabled, setEnabled] = useState(!!workflow.enabled);
  const [baseline, setBaseline] = useState(workflow.odataEtag);
  if (workflow.odataEtag !== baseline) {
    setBaseline(workflow.odataEtag);
    setEnabled(!!workflow.enabled);
  }
  return (
    <Checkbox
      aria-label={`${workflow.name} enabled`}
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
