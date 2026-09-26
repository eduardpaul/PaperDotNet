import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ChevronRight, Inbox } from 'lucide-react';
import { homeQuery } from '@/api/queries';
import { Card } from '@/components/ui/card';
import { listBuilder } from '@/features/lists/queries';

/** How many documents wait in the personal Inbox (LST-07). */
export function InboxCard() {
  const { data: home } = useQuery(homeQuery);
  const { data: count } = useQuery({
    queryKey: ['workspaces', home?.workspaceId, 'lists', home?.inboxListId, 'items', 'count'],
    enabled: !!home?.inboxListId,
    queryFn: async () =>
      (
        await listBuilder(home!.workspaceId!, home!.inboxListId!).items.get({
          queryParameters: { filter: 'isFolder eq false', top: 1, count: true },
        })
      )?.odataCount ?? 0,
  });

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
