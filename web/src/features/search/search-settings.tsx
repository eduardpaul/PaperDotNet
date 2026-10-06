import type { BuiltInWorkflowResponse, WorkflowResponse } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { toast } from 'sonner';
import { useState } from 'react';
import { keys } from '@/api/keys';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Checkbox } from '@/components/ui/select';
import { libraryWorkflowsQuery } from '@/features/documents/queries';
import { useCustomizeListWorkflow } from '@/features/workflows/customize';
import { workflowsQuery } from '@/features/workflows/queries';
import { workspaceQuery } from '@/features/workspaces/queries';
import { listBuilder } from '@/features/lists/queries';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

/** The process role of search indexing: the built-in, its alternatives and this list's own copies fill it, one at a time. */
export const indexRole = 'search.index';

export function SearchSettings({ workspaceId, listId }: { workspaceId: string; listId: string }) {
  const queryClient = useQueryClient();
  const [pendingInclusion, setPendingInclusion] = useState<boolean>();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const canManage = workspace?.access === 'manage';
  const queryKey = keys.searchSettings(workspaceId, listId);
  const { data: settings } = useQuery({
    queryKey,
    queryFn: () => listBuilder(workspaceId, listId).searchSettings.get(),
  });
  const save = useMutation({
    meta: { silent: true },
    mutationFn: (included: boolean) =>
      listBuilder(workspaceId, listId).searchSettings.put({ included }, ifMatch(settings)),
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKey, updated);
      toast.success(updated?.included ? 'Included in search. Reindexing requested.' : 'Excluded from search.');
    },
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) });
      await queryClient.invalidateQueries({ queryKey: ['search'] });
      setPendingInclusion(undefined);
    },
  });
  return (
    <SettingsSection title="Search" description="Choose whether items from this list appear in search.">
      {!settings ? (
        <Skeleton className="h-12" />
      ) : (
        <label className="flex items-start gap-3">
          <Checkbox
            aria-label="Include this list in search"
            checked={pendingInclusion ?? !!settings.included}
            disabled={!canManage || save.isPending}
            onChange={(event) => {
              setPendingInclusion(event.target.checked);
              save.mutate(event.target.checked);
            }}
          />
          <span className="flex flex-col gap-1">
            <span className="text-sm">Include this list in search</span>
            <span className="text-xs text-muted">
              Excluding hides titles, fields, comments and file text immediately. Including requests a rebuild through
              indexing workflows.
            </span>
          </span>
        </label>
      )}
      {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
    </SettingsSection>
  );
}

/**
 * The workflow that indexes this list for search (the search.index role): the built-in, an alternative such as AI context,
 * or a copy of its own that developers change (other steps, chunks of their own). One is active at a time.
 */
export function IndexingPipeline({ workspaceId, listId }: { workspaceId: string; listId: string }) {
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const canManage = workspace?.access === 'manage';
  const { data: builtIns } = useQuery(libraryWorkflowsQuery(workspaceId, listId));
  const { data: workflows } = useQuery(workflowsQuery(workspaceId));
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: libraryWorkflowsQuery(workspaceId, listId).queryKey }),
      queryClient.invalidateQueries({ queryKey: workflowsQuery(workspaceId).queryKey }),
    ]);
  const use = useMutation({
    mutationFn: (workflow: BuiltInWorkflowResponse) =>
      listBuilder(workspaceId, listId)
        .workflows.builtIns.byKey(workflow.key!)
        .put({ enabled: true }, ifMatch(workflow)),
    onSuccess: async (_, workflow) => {
      toast.success(`${workflow.name} now indexes this list.`);
      await refresh();
    },
    onError: (error) => toast.error(problemMessage(error)),
  });
  const customize = useCustomizeListWorkflow(workspaceId, listId);

  const pipelines = builtIns?.filter((w) => w.role === indexRole) ?? [];
  const copies: WorkflowResponse[] =
    workflows?.filter((w) => w.provides === indexRole && w.listId === listId && !w.builtIn) ?? [];
  if (!builtIns || !workflows) return <Skeleton className="h-24" />;
  const active = copies.find((w) => w.enabled) ?? pipelines.find((w) => w.enabled);
  return (
    <SettingsSection
      title="Indexing pipeline"
      description="The workflow that indexes this list for search. Switch to another, or copy one to change its steps; one is active at a time."
    >
      <ul className="divide-y" aria-label="Indexing pipelines">
        {pipelines.map((workflow) => (
          <li key={workflow.key} className="flex items-center gap-3 py-2">
            <span className="min-w-0 flex-1">
              <span className="block text-sm font-medium">{workflow.name}</span>
              <span className="block text-xs text-muted">{workflow.description}</span>
            </span>
            {workflow === active && <Badge tone="success">Active</Badge>}
            {!workflow.available && <Badge>Needs {workflow.requires}</Badge>}
            {canManage && workflow !== active && workflow.available && (
              <Button size="sm" disabled={use.isPending} onClick={() => use.mutate(workflow)}>
                Use
              </Button>
            )}
            {canManage && workflow.available && (
              <Button
                size="sm"
                variant="ghost"
                disabled={customize.isPending}
                onClick={() => customize.mutate(workflow)}
              >
                Customize
              </Button>
            )}
          </li>
        ))}
        {copies.map((workflow) => (
          <li key={workflow.id} className="flex items-center gap-3 py-2">
            <span className="min-w-0 flex-1">
              <span className="block text-sm font-medium">{workflow.name}</span>
              <span className="block text-xs text-muted">This list's own pipeline (version {workflow.version}).</span>
            </span>
            {workflow === active && <Badge tone="success">Active</Badge>}
            <Button asChild size="sm" variant="ghost">
              <Link to="/w/$workspaceId/settings/workflows" params={{ workspaceId }} search={{ edit: workflow.id! }}>
                Edit
              </Link>
            </Button>
          </li>
        ))}
      </ul>
      {!active && <p className="mt-2 text-xs text-muted">Automatic indexing is off; items are indexed on demand.</p>}
    </SettingsSection>
  );
}
