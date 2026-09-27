import { all, toArray } from '@paperdotnet/client';
import { queryOptions, useQuery } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { listBuilder } from '@/features/lists/queries';

export const listPermissionsQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: [...keys.list(workspaceId, listId), 'permissions'],
    queryFn: async () => (await listBuilder(workspaceId, listId).permissions.get())!,
  });

export const documentSettingsQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: [...keys.list(workspaceId, listId), 'documentSettings'],
    queryFn: async () => (await listBuilder(workspaceId, listId).documentSettings.get())!,
  });

/** The organization's content types (LST-02). */
export const contentTypesQuery = queryOptions({
  queryKey: ['contentTypes'],
  queryFn: async () => (await api.v10.contentTypes.get()) ?? [],
});

export const fieldTypesQuery = queryOptions({
  queryKey: ['fieldTypes'],
  queryFn: async () => (await api.v10.fieldTypes.get()) ?? [],
  staleTime: Infinity,
});

export const termSetsQuery = queryOptions({
  queryKey: ['termStore', 'sets'],
  queryFn: async () => toArray(all(api.v10.termStore.sets, { queryParameters: { top: 200 } })),
  staleTime: 5 * 60_000,
});

/** True when the caller manages the list (its settings are editable). */
export function useCanManageList(workspaceId: string, listId: string): boolean {
  return useListAccess(workspaceId, listId).canManage;
}

const levelRank = { none: 0, read: 1, contribute: 2, manage: 3 } as const;

/** What the caller may do. Unknown or still loading means the write actions stay hidden. */
export function accessOf(level: string | null | undefined) {
  const rank = levelRank[level as keyof typeof levelRank] ?? 0;
  return { canContribute: rank >= levelRank.contribute, canManage: rank >= levelRank.manage };
}

export function useListAccess(workspaceId: string, listId: string) {
  const { data, isPending } = useQuery(listPermissionsQuery(workspaceId, listId));
  return { ...accessOf(data?.effectiveLevel), isPending };
}

export const itemPermissionsQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: [...keys.item(workspaceId, listId, itemId), 'permissions'],
    queryFn: async () => (await listBuilder(workspaceId, listId).items.byItemId(itemId).permissions.get())!,
  });

export function useItemAccess(workspaceId: string, listId: string, itemId: string) {
  const { data, isPending } = useQuery({ ...itemPermissionsQuery(workspaceId, listId, itemId), enabled: !!itemId });
  return { ...accessOf(data?.effectiveLevel), isPending };
}
