// Where an item opens: its list with the item panel (frontend.md, "everything has a URL").
export function itemLink(item: { workspaceId?: string | null; listId?: string | null; itemId?: string | null }) {
  if (!item.workspaceId || !item.listId || !item.itemId) return undefined;
  return {
    to: '/w/$workspaceId/l/$listId' as const,
    params: { workspaceId: item.workspaceId, listId: item.listId },
    search: { item: item.itemId },
  };
}
