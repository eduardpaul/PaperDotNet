import { queryOptions } from '@tanstack/react-query';
import { keys } from '@/api/keys';
import { listBuilder } from '@/features/lists/queries';

/** A document's file versions (DOC-03); under the item's key, so live processing events refresh it. */
export const fileVersionsQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: [...keys.item(workspaceId, listId, itemId), 'file'],
    queryFn: async () =>
      (await listBuilder(workspaceId, listId).items.byItemId(itemId).file.versions.get())?.value ?? [],
  });
