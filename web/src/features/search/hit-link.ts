import type { SearchHit } from '@paperdotnet/client';

/** Where a hit opens: its item in its list; document hits open the preview on the matching page (SRC-09). */
export function hitLink(hit: SearchHit) {
  if (!hit.workspaceId || !hit.containerId || !hit.id) return undefined;
  return {
    to: '/w/$workspaceId/l/$listId' as const,
    params: { workspaceId: hit.workspaceId, listId: hit.containerId },
    search: hit.page ? { item: hit.id, tab: 'preview', page: hit.page } : { item: hit.id },
  };
}
