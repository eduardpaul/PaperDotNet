// Server data of lists and items. Item pages are infinite queries that follow @odata.nextLink.
import type { ItemPage, ItemResponse } from '@paperdotnet/client';
import { all, toArray } from '@paperdotnet/client';
import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';

export const listBuilder = (workspaceId: string, listId: string) =>
  api.v10.workspaces.byWorkspaceId(workspaceId).lists.byListId(listId);

export const listQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: keys.list(workspaceId, listId),
    queryFn: async () => (await listBuilder(workspaceId, listId).get())!,
  });

export const viewsQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: [...keys.list(workspaceId, listId), 'views'],
    queryFn: async () => (await listBuilder(workspaceId, listId).views.get()) ?? [],
  });

export interface ItemsQuery {
  filter?: string;
  orderby?: string;
  viewId?: string;
}

export const itemsQuery = (workspaceId: string, listId: string, query: ItemsQuery) =>
  infiniteQueryOptions({
    queryKey: [...keys.items(workspaceId, listId), query],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }): Promise<ItemPage> => {
      const items = listBuilder(workspaceId, listId).items;
      const page = pageParam
        ? await items.withUrl(pageParam).get()
        : await items.get({
            queryParameters: {
              filter: query.filter || undefined,
              orderby: query.orderby || undefined,
              viewId: query.viewId || undefined,
              top: 100,
              count: true,
            },
          });
      return page ?? { value: [] };
    },
    getNextPageParam: (last) => last.odataNextLink ?? undefined,
  });

export const itemQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: keys.item(workspaceId, listId, itemId),
    queryFn: async (): Promise<ItemResponse> => (await listBuilder(workspaceId, listId).items.byItemId(itemId).get())!,
  });

export const versionsQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: [...keys.item(workspaceId, listId, itemId), 'versions'],
    queryFn: () => toArray(all(listBuilder(workspaceId, listId).items.byItemId(itemId).versions)),
  });

export const recycleBinQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: [...keys.list(workspaceId, listId), 'recycleBin'],
    queryFn: () => toArray(all(listBuilder(workspaceId, listId).recycleBin)),
    staleTime: 0,
  });

/** OData string literal: quotes doubled. */
export function odataString(value: string): string {
  return `'${value.replaceAll("'", "''")}'`;
}
