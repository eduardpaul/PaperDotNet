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
  const { data } = useQuery(listPermissionsQuery(workspaceId, listId));
  return data?.effectiveLevel === 'manage';
}
