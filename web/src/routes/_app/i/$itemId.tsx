import { createFileRoute, redirect } from '@tanstack/react-router';
import { api } from '@/api/client';

/** Resolves a permanent item URL to its current list, including after moves between workspaces. */
export const Route = createFileRoute('/_app/i/$itemId')({
  loader: async ({ params }) => {
    const located = await api.v10.items.byItemId(params.itemId).get();
    throw redirect({
      to: '/w/$workspaceId/l/$listId',
      params: { workspaceId: located!.workspaceId!, listId: located!.item!.listId! },
      search: { item: params.itemId },
      replace: true,
    });
  },
});
