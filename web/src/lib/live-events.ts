// Live updates (API-07): server-sent events invalidate the cached data they touch, so every screen stays current.
import { subscribeLiveEvents } from '@paperdotnet/client';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { toast } from 'sonner';
import { client, signIn } from '@/api/client';
import { keys } from '@/api/keys';

export function useLiveEvents() {
  const queryClient = useQueryClient();

  useEffect(() => {
    let connectedBefore = false;
    const subscription = subscribeLiveEvents(client, {
      connected: () => {
        // After a reconnect, events may have been missed while the stream was down.
        if (connectedBefore) void queryClient.invalidateQueries();
        connectedBefore = true;
      },
      notification: (notification) => {
        void queryClient.invalidateQueries({ queryKey: keys.notifications });
        toast(notification.title, { description: notification.body ?? undefined });
      },
      'document.processing': (event) => {
        void queryClient.invalidateQueries({ queryKey: keys.item(event.workspaceId, event.listId, event.itemId) });
        void queryClient.invalidateQueries({ queryKey: keys.items(event.workspaceId, event.listId), exact: false });
      },
      operation: (operation) => {
        void queryClient.invalidateQueries({ queryKey: ['operations', operation.id] });
      },
      error: (error) => {
        if (error.closed && error.status === 401) void signIn();
      },
    });
    return () => subscription.close();
  }, [queryClient]);
}
