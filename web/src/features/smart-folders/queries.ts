import type { SmartFolderEntry } from '@paperdotnet/client';
import { all, toArray } from '@paperdotnet/client';
import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';

export const smartFoldersQuery = queryOptions({
  queryKey: keys.smartFolders,
  queryFn: () => toArray(all(api.v10.smartFolders, { queryParameters: { top: 200 } })),
});

export const smartFolderQuery = (id: string) =>
  queryOptions({
    queryKey: [...keys.smartFolders, id],
    queryFn: async () => (await api.v10.smartFolders.byId(id).get())!,
  });

/** The next level of a folder's virtual tree (TAX-10). */
export const groupsQuery = (id: string, path: string[]) =>
  queryOptions({
    queryKey: [...keys.smartFolders, id, 'groups', path],
    queryFn: async () => (await api.v10.smartFolders.byId(id).groups.get({ queryParameters: { path } }))!,
  });

export const folderItemsQuery = (id: string, path: string[]) =>
  infiniteQueryOptions({
    queryKey: [...keys.smartFolders, id, 'items', path],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }): Promise<{ value?: SmartFolderEntry[] | null; odataNextLink?: string | null }> => {
      const items = api.v10.smartFolders.byId(id).items;
      return (
        (pageParam
          ? await items.withUrl(pageParam).get()
          : await items.get({ queryParameters: { path, top: 100 } })) ?? { value: [] }
      );
    },
    getNextPageParam: (last) => last.odataNextLink ?? undefined,
  });

/** The drag data of an item (list rows are dragged onto smart folders in the sidebar, TAX-09). */
export const ItemDragType = 'application/x-paperdotnet-item';

export interface DraggedItem {
  workspaceId: string;
  listId: string;
  itemId: string;
  title: string;
}
