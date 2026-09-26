import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { Briefcase, Library } from 'lucide-react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { listsQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { ListIcon } from '@/features/lists/list-icon';
import { useFormat } from '@/lib/preferences';

export const workspaceQuery = (workspaceId: string) => ({
  queryKey: keys.workspace(workspaceId),
  queryFn: async () => (await api.v10.workspaces.byWorkspaceId(workspaceId).get())!,
});

export const Route = createFileRoute('/_app/w/$workspaceId/')({
  loader: ({ context, params }) => context.queryClient.ensureQueryData(workspaceQuery(params.workspaceId)),
  component: Workspace,
});

function Workspace() {
  const { workspaceId } = Route.useParams();
  const format = useFormat();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const { data: lists, isPending } = useQuery(listsQuery(workspaceId));

  return (
    <Page>
      <PageHeader icon={Briefcase} title={workspace?.name} description={workspace?.description} />
      <h2 className="mb-3 text-xs font-medium tracking-wide text-muted uppercase">Lists and libraries</h2>
      {isPending ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : lists?.length ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {lists.map((list) => (
            <Card key={list.id} className="flex flex-col gap-2 p-4">
              <div className="flex items-center gap-2">
                <ListIcon list={list} className="size-4 text-accent" />
                <h3 className="truncate font-semibold">{list.name}</h3>
              </div>
              <p className="line-clamp-2 flex-1 text-[13px] text-muted">
                {list.description || (list.kind === 'library' ? 'Library' : 'List')}
              </p>
              <p className="text-xs text-muted">Updated {format.relative(list.updatedAt)}</p>
            </Card>
          ))}
        </div>
      ) : (
        <Card>
          <EmptyState icon={Library} title="No lists yet">
            Lists and libraries hold the documents, tasks, events and notes of this workspace.
          </EmptyState>
        </Card>
      )}
    </Page>
  );
}
