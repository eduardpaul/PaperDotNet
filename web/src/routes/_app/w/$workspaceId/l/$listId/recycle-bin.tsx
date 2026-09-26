import { fieldsOf } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link } from '@tanstack/react-router';
import { ArrowLeft, RotateCcw, Trash2 } from 'lucide-react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { userName, useUsers } from '@/features/fields/directory';
import { listBuilder, listQuery, recycleBinQuery } from '@/features/lists/queries';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/recycle-bin')({ component: RecycleBin });

/** Deleted items of a list (LST-13): restore or delete for good; kept 93 days. */
function RecycleBin() {
  const { workspaceId, listId } = Route.useParams();
  const format = useFormat();
  const users = useUsers();
  const queryClient = useQueryClient();
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  const { data, isPending } = useQuery(recycleBinQuery(workspaceId, listId));
  const invalidate = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: recycleBinQuery(workspaceId, listId).queryKey }),
      queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, listId) }),
    ]);
  const restore = useMutation({
    mutationFn: (itemId: string) => listBuilder(workspaceId, listId).recycleBin.byItemId(itemId).restore.post(),
    onSuccess: async () => {
      toast.success('Restored.');
      await invalidate();
    },
  });
  const purge = useMutation({
    mutationFn: (itemId: string) => listBuilder(workspaceId, listId).recycleBin.byItemId(itemId).delete(),
    onSuccess: async () => {
      toast.success('Deleted for good.');
      await invalidate();
    },
  });

  return (
    <Page className="max-w-4xl">
      <Link
        to="/w/$workspaceId/l/$listId"
        params={{ workspaceId, listId }}
        className="mb-3 inline-flex items-center gap-1 text-[13px] text-muted hover:text-foreground"
      >
        <ArrowLeft className="size-4" /> {list?.name}
      </Link>
      <PageHeader
        icon={Trash2}
        title="Recycle bin"
        description="Deleted items stay here for 93 days before they are removed for good."
      />
      <Card>
        {isPending ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10" />
            <Skeleton className="h-10" />
          </div>
        ) : data?.length ? (
          <ul className="divide-y">
            {data.map((entry) => (
              <li key={entry.item?.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13px] font-medium">{String(fieldsOf(entry.item).title ?? 'Untitled')}</p>
                  <p className="text-xs text-muted">
                    Deleted {format.relative(entry.deletedAt)} by{' '}
                    {userName(users.get(entry.deletedBy ?? ''), entry.deletedBy ?? '')}
                  </p>
                </div>
                <Button size="sm" disabled={restore.isPending} onClick={() => restore.mutate(entry.item!.id!)}>
                  <RotateCcw /> Restore
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  disabled={purge.isPending}
                  onClick={() => purge.mutate(entry.item!.id!)}
                >
                  Delete for good
                </Button>
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState icon={Trash2} title="The recycle bin is empty" />
        )}
      </Card>
    </Page>
  );
}
