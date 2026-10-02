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
      'document.changed': (event) => {
        // A library workflow made text, a thumbnail, pages or an OCR version (ADR-0038): the item, its list and its images.
        void queryClient.invalidateQueries({ queryKey: keys.item(event.workspaceId, event.listId, event.itemId) });
        void queryClient.invalidateQueries({ queryKey: keys.items(event.workspaceId, event.listId), exact: false });
        void queryClient.invalidateQueries({
          predicate: (query) => query.queryKey[0] === 'blob' && String(query.queryKey[1]).includes(event.itemId),
        });
      },
      'item.changed': (event) => {
        void queryClient.invalidateQueries({ queryKey: keys.globalItemResources });
        void queryClient.invalidateQueries({ queryKey: keys.item(event.workspaceId, event.listId, event.itemId) });
        void queryClient.invalidateQueries({ queryKey: keys.items(event.workspaceId, event.listId), exact: false });
        void queryClient.invalidateQueries({ queryKey: ['me', 'tasks'] });
        void queryClient.invalidateQueries({ queryKey: ['me', 'calendar'] });
        void queryClient.invalidateQueries({ queryKey: ['me', 'home'] });
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
