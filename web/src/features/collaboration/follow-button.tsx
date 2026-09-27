import type { ItemResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, BellOff } from 'lucide-react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { subscriptionsQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import { Tooltip } from '@/components/ui/popover';

/**
 * Follow a list, a folder or an item (NTF-03). Omit `item` to follow the whole list.
 * Smart folders and saved searches have no follow target on the server.
 */
export function FollowButton({
  workspaceId,
  listId,
  item,
  label,
}: {
  workspaceId: string;
  listId: string;
  item?: Pick<ItemResponse, 'id' | 'isFolder'>;
  /** Accessible name when the button sits in a toolbar, e.g. "Follow this list". */
  label?: string;
}) {
  const queryClient = useQueryClient();
  const { data } = useQuery(subscriptionsQuery);
  const subscription = data?.find((s) => s.listId === listId && (item ? s.itemId === item.id : !s.itemId));
  const what = item ? (item.isFolder ? 'folder' : 'item') : 'list';
  const toggle = useMutation({
    mutationFn: async () => {
      if (subscription) await api.v10.me.subscriptions.byId(subscription.id!).delete();
      else
        await api.v10.me.subscriptions.post({
          workspaceId,
          listId,
          itemId: item?.id,
          frequency: 'immediate',
        });
    },
    onSuccess: async () => {
      toast.success(subscription ? `You no longer follow this ${what}.` : `You follow this ${what}.`);
      await queryClient.invalidateQueries({ queryKey: subscriptionsQuery.queryKey });
    },
  });
  return (
    <Tooltip label={subscription ? 'Stop following' : `Follow this ${what}`}>
      <Button
        variant="ghost"
        size="icon"
        aria-pressed={!!subscription}
        aria-label={label ?? (subscription ? 'Following' : 'Follow')}
        disabled={toggle.isPending}
        onClick={() => toggle.mutate()}
      >
        {subscription ? <BellOff className="text-accent" /> : <Bell />}
      </Button>
    </Tooltip>
  );
}
