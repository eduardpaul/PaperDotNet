import { queryOptions, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ChevronRight, Files, Inbox } from 'lucide-react';
import { homeQuery } from '@/api/queries';
import { Card } from '@/components/ui/card';
import { listBuilder } from '@/features/lists/queries';

/** How many documents wait in the personal Inbox (LST-07). Shared with the sidebar badge. */
export const inboxCountQuery = (workspaceId?: string | null, inboxListId?: string | null) =>
  queryOptions({
    queryKey: ['workspaces', workspaceId, 'lists', inboxListId, 'items', 'count'],
    enabled: !!workspaceId && !!inboxListId,
    queryFn: async () =>
      (
        await listBuilder(workspaceId!, inboxListId!).items.get({
          queryParameters: { filter: 'isFolder eq false', top: 1, count: true },
        })
      )?.odataCount ?? 0,
  });

/** How many documents wait in the personal Inbox (LST-07). */
export function InboxCard() {
  const { data: home } = useQuery(homeQuery);
  const { data: count } = useQuery(inboxCountQuery(home?.workspaceId, home?.inboxListId));

  return (
    <Card>
      <Link to="/inbox" className="flex items-center gap-3 rounded-lg px-4 py-3 hover:bg-surface-muted/60">
        <span className="rounded-md bg-accent-soft p-2 text-accent">
          <Inbox className="size-4" />
        </span>
        <span className="flex-1">
          <span className="block text-sm font-semibold">Inbox</span>
          <span className="text-xs text-muted">
            {count ? `${count} ${count === 1 ? 'document' : 'documents'} to file` : 'Nothing to file'}
          </span>
        </span>
        <ChevronRight className="size-4 text-muted" />
      </Link>
    </Card>
  );
}

/** The personal Documents library, where filed scans live. */
export function DocumentsCard() {
  const { data: home } = useQuery(homeQuery);
  if (!home?.workspaceId || !home.documentsListId) return null;
  return (
    <Card>
      <Link
        to="/w/$workspaceId/l/$listId"
        params={{ workspaceId: home.workspaceId, listId: home.documentsListId }}
        className="flex items-center gap-3 rounded-lg px-4 py-3 hover:bg-surface-muted/60"
      >
        <span className="rounded-md bg-accent-soft p-2 text-accent">
          <Files className="size-4" />
        </span>
        <span className="flex-1">
          <span className="block text-sm font-semibold">Documents</span>
          <span className="text-xs text-muted">Your filed files</span>
        </span>
        <ChevronRight className="size-4 text-muted" />
      </Link>
    </Card>
  );
}
