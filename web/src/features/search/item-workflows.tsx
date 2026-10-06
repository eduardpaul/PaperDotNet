import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { documentRunsQuery } from '@/features/documents/queries';
import { listBuilder } from '@/features/lists/queries';
import { workspaceBuilder, workspaceQuery } from '@/features/workspaces/queries';
import { problemMessage } from '@/lib/errors';

const busy = new Set(['running', 'waiting']);

const labels: Record<string, string> = {
  excluded: 'Excluded',
  notIndexed: 'Not indexed',
  running: 'Running',
  waiting: 'Waiting',
  indexed: 'Indexed',
  stale: 'Stale',
  failed: 'Failed',
};

export function ItemWorkflows({
  workspaceId,
  listId,
  itemId,
  canWrite,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  canWrite: boolean;
}) {
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  // Quickly while indexing is going, slowly otherwise (automatic runs start in the background after a change).
  const { data: status, isError } = useQuery({
    queryKey: keys.searchIndex(workspaceId, listId, itemId),
    queryFn: () => listBuilder(workspaceId, listId).items.byItemId(itemId).searchIndex.get(),
    refetchInterval: (query) => (busy.has(query.state.data?.state ?? '') ? 2000 : 15000),
  });
  const { data: runs } = useQuery(documentRunsQuery(workspaceId, listId, itemId));
  const refresh = () => queryClient.invalidateQueries({ queryKey: keys.item(workspaceId, listId, itemId) });
  const index = useMutation({
    mutationFn: () =>
      listBuilder(workspaceId, listId)
        .workflows.builtIns.byKey('search.index')
        .runs.post({ listId, itemIds: [itemId] }),
    onSuccess: () => toast.success('Indexing started.'),
    onSettled: refresh,
    onError: (error) => toast.error(problemMessage(error)),
  });
  const retry = useMutation({
    mutationFn: (id: string) => workspaceBuilder(workspaceId).workflows.runs.byId(id).retry.post(),
    onSuccess: () => toast.success('Retry started.'),
    onSettled: refresh,
    onError: (error) => toast.error(problemMessage(error)),
  });
  return (
    <div className="space-y-5">
      <section aria-label="Search indexing" className="space-y-2 rounded-lg border p-3">
        <div className="flex items-center gap-2">
          <h3 className="flex-1 text-sm font-semibold">Search indexing</h3>
          {status && (
            <Badge tone={status.state === 'indexed' ? 'success' : status.state === 'failed' ? 'warning' : undefined}>
              {labels[status.state ?? 'notIndexed'] ?? status.state}
            </Badge>
          )}
          {canWrite && (
            <Button size="sm" disabled={!status?.included || index.isPending} onClick={() => index.mutate()}>
              Index now
            </Button>
          )}
        </div>
        {!status && !isError && <Skeleton className="h-5" />}
        {isError && <Alert>Could not load indexing status.</Alert>}
        {status?.indexed && (
          <p className="text-xs text-muted">
            {status.chunks ?? 0} chunks published.{' '}
            {status.embeddingState === 'ready'
              ? 'Embeddings ready.'
              : status.embeddingState === 'pending'
                ? 'Embeddings pending.'
                : 'Embeddings unavailable.'}
          </p>
        )}
        {status?.contentState === 'pending' && (
          <p className="text-xs text-muted">File text is waiting for the document text workflow.</p>
        )}
        {status?.contentState === 'noText' && (
          <p className="text-xs text-muted">No text was extracted from this file.</p>
        )}
        {status?.state === 'excluded' && <p className="text-xs text-muted">This list is excluded from search.</p>}
        {status?.truncated && <Alert>Some text exceeds this workflow's chunk limit.</Alert>}
      </section>
      <section aria-label="Workflow history" className="space-y-2">
        <h3 className="text-sm font-semibold">Workflow history</h3>
        {runs?.length === 0 && <p className="text-sm text-muted">No workflows have run yet.</p>}
        {runs?.map((run) => (
          <details key={run.id} className="rounded-lg border p-3">
            <summary className="cursor-pointer text-sm">
              {run.workflow ?? 'Deleted workflow'} · {run.status}
            </summary>
            <div className="mt-3 space-y-2">
              {run.node && <p className="text-xs text-muted">Step: {run.node}</p>}
              {run.errorEscaped && <Alert>{run.errorEscaped}</Alert>}
              {workspace?.access === 'manage' && run.status === 'failed' && (
                <Button size="sm" disabled={retry.isPending} onClick={() => retry.mutate(run.id!)}>
                  Retry failed step
                </Button>
              )}
              <pre className="text-xs whitespace-pre-wrap text-muted">{JSON.stringify(run.log, null, 2)}</pre>
            </div>
          </details>
        ))}
      </section>
    </div>
  );
}
