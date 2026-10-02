import { queryOptions } from '@tanstack/react-query';
import { keys } from '@/api/keys';
import { listBuilder } from '@/features/lists/queries';

export const calendarSourcesQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: keys.calendarSources(workspaceId, listId),
    queryFn: async () => (await listBuilder(workspaceId, listId).calendarSources.get()) ?? [],
    refetchInterval: 3000,
  });

export const itemCalendarSourceQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: [...keys.item(workspaceId, listId, itemId), 'calendarSource'],
    queryFn: async () => (await listBuilder(workspaceId, listId).items.byItemId(itemId).calendarSource.get())!,
  });
