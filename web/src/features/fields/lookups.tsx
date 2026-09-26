// Resolves the ids in field values of a page of items (people, terms, looked-up items) with a few batched requests,
// and shares the names with every cell and editor below through context.
import type { TermResponse, UserResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { useQueries, useQuery } from '@tanstack/react-query';
import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { listsQuery, workspacesQuery } from '@/api/queries';
import { listBuilder } from '@/features/lists/queries';
import { useTerms, useUsers } from './directory';
import { idsOf, type FieldDefinition } from './values';

export interface ValueNames {
  users: Map<string, UserResponse>;
  terms: Map<string, TermResponse>;
  /** Titles of looked-up items by id. */
  items: Map<string, string>;
}

const NamesContext = createContext<ValueNames>({ users: new Map(), terms: new Map(), items: new Map() });

export function useValueNames(): ValueNames {
  return useContext(NamesContext);
}

/** Where a list lives (lookup fields name a list, and item URLs need its workspace). */
export function useListWorkspace(listId: string | null | undefined): string | undefined {
  const { data: workspaces } = useQuery(workspacesQuery);
  const lists = useQueries({
    queries: (listId ? (workspaces ?? []) : []).map((w) => ({ ...listsQuery(w.id!), staleTime: 5 * 60_000 })),
  });
  return lists.flatMap((q) => q.data ?? []).find((l) => l.id === listId)?.workspaceId ?? undefined;
}

function LookupTitles({
  listId,
  ids,
  children,
}: {
  listId: string;
  ids: string[];
  children: (titles: Map<string, string>) => ReactNode;
}) {
  const workspaceId = useListWorkspace(listId);
  const sorted = [...new Set(ids)].sort();
  const { data } = useQuery({
    queryKey: ['lookupTitles', listId, sorted],
    enabled: !!workspaceId && sorted.length > 0,
    staleTime: 60_000,
    queryFn: async () =>
      (
        await listBuilder(workspaceId!, listId).items.get({
          queryParameters: { filter: `id in (${sorted.slice(0, 100).join(',')})`, select: 'title', top: 100 },
        })
      )?.value ?? [],
  });
  const titles = new Map<string, string>();
  for (const item of data ?? []) titles.set(item.id!, String(fieldsOf(item).title ?? 'Untitled'));
  return <>{children(titles)}</>;
}

/** Provides the names of the values of these fields in these items (their field values). */
export function ValueNamesProvider({
  fields,
  values,
  children,
}: {
  fields: FieldDefinition[];
  values: Record<string, unknown>[];
  children: ReactNode;
}) {
  const users = useUsers();
  const termIds = useMemo(
    () =>
      fields
        .filter((f) => f.type === 'managedMetadata' || f.type === 'keywords')
        .flatMap((f) => values.flatMap((v) => idsOf(v[f.name!]))),
    [fields, values],
  );
  const terms = useTerms(termIds);
  const lookups = useMemo(() => {
    const byList = new Map<string, string[]>();
    for (const field of fields.filter((f) => f.type === 'lookup' && f.lookupListId)) {
      const ids = values.flatMap((v) => idsOf(v[field.name!]));
      if (ids.length) byList.set(field.lookupListId!, [...(byList.get(field.lookupListId!) ?? []), ...ids]);
    }
    return [...byList.entries()];
  }, [fields, values]);

  const render = (index: number, items: Map<string, string>): ReactNode => {
    if (index >= lookups.length)
      return <NamesContext.Provider value={{ users, terms, items }}>{children}</NamesContext.Provider>;
    const [listId, ids] = lookups[index]!;
    return (
      <LookupTitles listId={listId} ids={ids}>
        {(titles) => render(index + 1, new Map([...items, ...titles]))}
      </LookupTitles>
    );
  };

  return render(0, new Map());
}

/** A term's label in the user's language, else its name. */
export function termLabel(term: TermResponse | undefined, language?: string): string | undefined {
  if (!term) return undefined;
  const lang = language?.toLowerCase();
  const label = lang
    ? term.labels?.find((l) => l.language?.toLowerCase() === lang || lang.startsWith(`${l.language?.toLowerCase()}-`))
    : undefined;
  return label?.name ?? term.name ?? undefined;
}
