import type { ListSummary } from '@paperdotnet/client';
import { useQueries, useQuery } from '@tanstack/react-query';
import { listsQuery, workspacesQuery } from '@/api/queries';

/** Every list made from a template (e.g. all task lists, all calendars), with its workspace's name. */
export function useListsByTemplate(templateKey: string): (ListSummary & { workspaceName: string })[] {
  const { data: workspaces } = useQuery(workspacesQuery);
  const names = new Map((workspaces ?? []).map((w) => [w.id, w.isPersonal ? 'My files' : (w.name ?? '')]));
  return useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) })
    .flatMap((q) => q.data ?? [])
    .filter((l) => l.templateKey === templateKey)
    .map((l) => ({ ...l, workspaceName: names.get(l.workspaceId) ?? '' }));
}

/** Remembers the last list chosen for quick actions (per browser; storage may be unavailable). */
export function rememberedList(key: string): string | undefined {
  try {
    return localStorage.getItem(`paperdotnet.${key}`) ?? undefined;
  } catch {
    return undefined;
  }
}

export function rememberList(key: string, listId: string) {
  try {
    localStorage.setItem(`paperdotnet.${key}`, listId);
  } catch {
    // Not remembered; nothing else changes.
  }
}
