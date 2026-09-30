import { queryOptions } from '@tanstack/react-query';
import { keys } from '@/api/keys';
import { listBuilder } from '@/features/lists/queries';
import { workspaceBuilder } from '@/features/workspaces/queries';

/** A document's workflow runs, newest first; under the item's key, so live document events refresh them. */
export const documentRunsQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: [...keys.item(workspaceId, listId, itemId), 'runs'],
    queryFn: async () =>
      (await workspaceBuilder(workspaceId).workflows.runs.get({ queryParameters: { itemId } }))?.value ?? [],
    // Runs end in the background: look again while one is going.
    refetchInterval: (query) =>
      query.state.data?.some((r) => r.status === 'running' || r.status === 'waiting') ? 2000 : false,
  });

/** A library's document workflows and whether each is on (ADR-0038). */
export const libraryWorkflowsQuery = (workspaceId: string, listId: string) =>
  queryOptions({
    queryKey: keys.libraryWorkflows(workspaceId, listId),
    queryFn: async () => (await listBuilder(workspaceId, listId).workflows.builtIns.get()) ?? [],
  });

/** A document's file versions (DOC-03); under the item's key, so live document events refresh it. */
export const fileVersionsQuery = (workspaceId: string, listId: string, itemId: string) =>
  queryOptions({
    queryKey: [...keys.item(workspaceId, listId, itemId), 'file'],
    queryFn: async () =>
      (await listBuilder(workspaceId, listId).items.byItemId(itemId).file.versions.get())?.value ?? [],
  });
