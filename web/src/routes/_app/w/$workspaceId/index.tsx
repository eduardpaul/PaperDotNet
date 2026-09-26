import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link } from '@tanstack/react-router';
import { Briefcase, Library, Plus, Settings } from 'lucide-react';
import { useState } from 'react';
import { listsQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { ListIcon } from '@/features/lists/list-icon';
import { NewListDialog } from '@/features/workspaces/new-list-dialog';
import { workspaceQuery } from '@/features/workspaces/queries';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/w/$workspaceId/')({
  loader: ({ context, params }) => context.queryClient.ensureQueryData(workspaceQuery(params.workspaceId)),
  component: Workspace,
});

function Workspace() {
  const { workspaceId } = Route.useParams();
  const format = useFormat();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const { data: lists, isPending } = useQuery(listsQuery(workspaceId));
  const [newList, setNewList] = useState(false);

  return (
    <Page>
      <PageHeader
        icon={Briefcase}
        title={workspace?.name}
        description={workspace?.description}
        actions={
          <>
            {workspace?.access === 'manage' && (
              <Button asChild>
                <Link to="/w/$workspaceId/settings" params={{ workspaceId }}>
                  <Settings /> Settings
                </Link>
              </Button>
            )}
            <Button variant="primary" onClick={() => setNewList(true)}>
              <Plus /> New list
            </Button>
          </>
        }
      />
      <h2 className="mb-3 text-xs font-medium tracking-wide text-muted uppercase">Lists and libraries</h2>
      {isPending ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : lists?.length ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {lists.map((list) => (
            <Link
              key={list.id}
              to="/w/$workspaceId/l/$listId"
              params={{ workspaceId, listId: list.id! }}
              className="group"
            >
              <Card className="flex h-full flex-col gap-2 p-4 transition-colors group-hover:border-accent/50">
                <div className="flex items-center gap-2">
                  <ListIcon list={list} className="size-4 text-accent" />
                  <h3 className="truncate font-semibold">{list.name}</h3>
                </div>
                <p className="line-clamp-2 flex-1 text-[13px] text-muted">
                  {list.description || (list.kind === 'library' ? 'Library' : 'List')}
                </p>
                <p className="text-xs text-muted">Updated {format.relative(list.updatedAt)}</p>
              </Card>
            </Link>
          ))}
        </div>
      ) : (
        <Card>
          <EmptyState icon={Library} title="No lists yet">
            <p className="mb-3">Lists and libraries hold the documents, tasks, events and notes of this workspace.</p>
            <Button variant="primary" onClick={() => setNewList(true)}>
              <Plus /> New list
            </Button>
          </EmptyState>
        </Card>
      )}
      <NewListDialog key={String(newList)} workspaceId={workspaceId} open={newList} onOpenChange={setNewList} />
    </Page>
  );
}
