import type { ItemResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, BellOff } from 'lucide-react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Tooltip } from '@/components/ui/popover';

const subscriptionsQuery = {
  queryKey: ['me', 'subscriptions'],
  queryFn: async () => (await api.v10.me.subscriptions.get()) ?? [],
};

/** Follow an item to be notified when it changes (NTF-03). */
export function FollowButton({
  workspaceId,
  listId,
  item,
}: {
  workspaceId: string;
  listId: string;
  item: ItemResponse;
}) {
  const queryClient = useQueryClient();
  const { data } = useQuery(subscriptionsQuery);
  const subscription = data?.find((s) => s.itemId === item.id);
  const toggle = useMutation({
    mutationFn: async () => {
      if (subscription) await api.v10.me.subscriptions.byId(subscription.id!).delete();
      else await api.v10.me.subscriptions.post({ workspaceId, listId, itemId: item.id, frequency: 'immediate' });
    },
    onSuccess: async () => {
      toast.success(
        subscription ? 'You no longer follow this item.' : 'You follow this item: changes will notify you.',
      );
      await queryClient.invalidateQueries({ queryKey: subscriptionsQuery.queryKey });
    },
  });
  return (
    <Tooltip label={subscription ? 'Stop following' : 'Follow: get notified of changes'}>
      <Button
        variant="ghost"
        size="icon"
        aria-pressed={!!subscription}
        aria-label={subscription ? 'Following' : 'Follow'}
        disabled={toggle.isPending}
        onClick={() => toggle.mutate()}
      >
        {subscription ? <BellOff className="text-accent" /> : <Bell />}
      </Button>
    </Tooltip>
  );
}
