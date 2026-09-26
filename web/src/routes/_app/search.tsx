import type { FacetValue, SearchFacets, SearchHit } from '@paperdotnet/client';
import { useInfiniteQuery, useQueries, useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { FileText, Search as SearchIcon, SearchX, X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { listsQuery, workspacesQuery } from '@/api/queries';
import { Page } from '@/components/page';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { Alert, EmptyState, Skeleton, Spinner } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { useTerms } from '@/features/fields/directory';
import { termLabel } from '@/features/fields/lookups';
import { Highlight, queryWords } from '@/features/search/highlight';
import { hitLink } from '@/features/search/hit-link';
import { contentTypesQuery, searchQuery, type SearchParams } from '@/features/search/queries';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

const text = (value: unknown) => (typeof value === 'string' && value ? value : undefined);
const modes = [
  { key: undefined, label: 'Best' },
  { key: 'keyword', label: 'Exact words' },
  { key: 'semantic', label: 'Meaning' },
] as const;

export const Route = createFileRoute('/_app/search')({
  validateSearch: (search: Record<string, unknown>): SearchParams => ({
    q: text(search.q),
    mode: search.mode === 'keyword' || search.mode === 'semantic' || search.mode === 'hybrid' ? search.mode : undefined,
    workspace: text(search.workspace),
    list: text(search.list),
    contentType: text(search.contentType),
    term: text(search.term),
  }),
  component: SearchPage,
});

/** One search over documents (with their text), items, tasks, events and notes (SRC-01…03, SRC-07…09). */
function SearchPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [query, setQuery] = useState(search.q ?? '');
  const results = useInfiniteQuery(searchQuery(search));
  const first = results.data?.pages[0];
  const hits = results.data?.pages.flatMap((p) => p.value ?? []) ?? [];
  const words = queryWords(search.q);
  const filtered = !!(search.workspace || search.list || search.contentType || search.term);
  const set = (patch: Partial<SearchParams>) => void navigate({ search: (c) => ({ ...c, ...patch }) });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    set({ q: query.trim() || undefined });
  };

  return (
    <Page>
      <form onSubmit={onSubmit} className="mb-4 flex gap-2" role="search">
        <div className="relative flex-1">
          <SearchIcon className="absolute top-3 left-3 size-4 text-muted" />
          <Input
            aria-label="Search"
            autoFocus
            placeholder='Search documents, tasks, events and notes — "exact phrase", -exclude, prefix*'
            className="h-10 pl-9 text-[15px]"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>
        <Button type="submit" variant="primary" size="lg">
          Search
        </Button>
      </form>
      <div className="mb-5 flex flex-wrap items-center gap-2">
        <div role="radiogroup" aria-label="Match" className="flex rounded-md bg-surface-muted p-0.5">
          {modes.map((m) => (
            <button
              key={m.label}
              type="button"
              role="radio"
              aria-checked={search.mode === m.key}
              onClick={() => set({ mode: m.key })}
              className={cn(
                'rounded px-2.5 py-1 text-xs font-medium text-muted',
                search.mode === m.key && 'bg-surface text-foreground shadow-xs',
              )}
            >
              {m.label}
            </button>
          ))}
        </div>
        {first && (
          <span className="text-xs text-muted">
            {first.odataCount ?? hits.length} {first.odataCount === 1 ? 'result' : 'results'}
            {first.mode && search.mode === undefined && <> · {first.mode}</>}
          </span>
        )}
        {filtered && (
          <Button
            size="sm"
            variant="ghost"
            onClick={() => set({ workspace: undefined, list: undefined, contentType: undefined, term: undefined })}
          >
            <X /> Clear filters
          </Button>
        )}
      </div>

      <div className="grid gap-6 lg:grid-cols-[14rem_1fr]">
        <aside aria-label="Filters" className="flex flex-col gap-5">
          {first?.facets && <Facets facets={first.facets} search={search} set={set} />}
        </aside>
        <div className="min-w-0">
          {!search.q && !filtered ? (
            <EmptyState icon={SearchIcon} title="Search everything you can read">
              Documents are found by their text and pages, including scanned ones after OCR.
            </EmptyState>
          ) : results.isPending ? (
            <div className="space-y-3">
              <Skeleton className="h-20" />
              <Skeleton className="h-20" />
              <Skeleton className="h-20" />
            </div>
          ) : results.isError ? (
            <Alert>{problemMessage(results.error)}</Alert>
          ) : hits.length === 0 ? (
            <EmptyState icon={SearchX} title="No results">
              Try other words, “Meaning” to match by sense, or fewer filters.
            </EmptyState>
          ) : (
            <>
              <ol className="flex flex-col gap-2">
                {hits.map((hit) => (
                  <HitRow key={`${hit.id}-${hit.page ?? ''}`} hit={hit} words={words} />
                ))}
              </ol>
              {results.hasNextPage && (
                <Button
                  className="mt-4"
                  disabled={results.isFetchingNextPage}
                  onClick={() => void results.fetchNextPage()}
                >
                  {results.isFetchingNextPage && <Spinner />} More results
                </Button>
              )}
            </>
          )}
        </div>
      </div>
    </Page>
  );
}

function HitRow({ hit, words }: { hit: SearchHit; words: string[] }) {
  const format = useFormat();
  const { data: workspaces } = useQuery(workspacesQuery);
  const { data: lists } = useQuery({ ...listsQuery(hit.workspaceId ?? ''), enabled: !!hit.workspaceId });
  const workspace = workspaces?.find((w) => w.id === hit.workspaceId);
  const list = lists?.find((l) => l.id === hit.containerId);
  const link = hitLink(hit);
  const body = (
    <Card className="flex gap-3 p-4 transition-colors hover:border-accent/50">
      <FileText className="mt-0.5 size-4 shrink-0 text-muted" />
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-2 font-medium">
          <span className="truncate">
            <Highlight text={hit.title ?? 'Untitled'} words={words} />
          </span>
          {hit.page && <Badge tone="accent">Page {hit.page}</Badge>}
          {hit.matchedBy?.includes('semantic') && !hit.matchedBy.includes('keyword') && <Badge>By meaning</Badge>}
        </p>
        {hit.snippet && (
          <p className="mt-1 line-clamp-2 text-[13px] text-muted">
            <Highlight text={hit.snippet} words={words} />
          </p>
        )}
        <p className="mt-1.5 text-xs text-muted">
          {workspace?.isPersonal ? 'My files' : workspace?.name}
          {list && <> › {list.name}</>} · {format.relative(hit.updatedAt)}
        </p>
      </div>
    </Card>
  );
  return <li>{link ? <Link {...link}>{body}</Link> : body}</li>;
}

function Facets({
  facets,
  search,
  set,
}: {
  facets: SearchFacets;
  search: SearchParams;
  set: (patch: Partial<SearchParams>) => void;
}) {
  const format = useFormat();
  const { data: workspaces } = useQuery(workspacesQuery);
  const lists = useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) }).flatMap((q) => q.data ?? []);
  const { data: contentTypes } = useQuery(contentTypesQuery);
  const terms = useTerms((facets.term ?? []).map((f) => f.value!).filter(Boolean));
  const name = {
    workspace: (id: string) => {
      const w = workspaces?.find((x) => x.id === id);
      return w?.isPersonal ? 'My files' : w?.name;
    },
    list: (id: string) => lists.find((l) => l.id === id)?.name,
    contentType: (id: string) => contentTypes?.find((c) => c.id === id)?.name,
    term: (id: string) => termLabel(terms.get(id), format.preferences.language),
  };

  const group = (title: string, key: keyof typeof name, values: FacetValue[] | null | undefined) =>
    values?.length ? (
      <section>
        <h2 className="mb-1.5 text-xs font-medium text-muted">{title}</h2>
        <ul className="flex flex-col gap-0.5">
          {values.slice(0, 8).map((f) => {
            const active = search[key] === f.value;
            return (
              <li key={f.value}>
                <button
                  type="button"
                  aria-pressed={active}
                  onClick={() => set({ [key]: active ? undefined : f.value! })}
                  className={cn(
                    'flex w-full items-center gap-2 rounded-md px-2 py-1 text-left text-[13px] hover:bg-surface-muted',
                    active && 'bg-accent-soft font-medium text-accent',
                  )}
                >
                  <span className="min-w-0 flex-1 truncate">{name[key](f.value!) ?? '…'}</span>
                  <span className="text-xs text-muted tabular-nums">{f.count}</span>
                </button>
              </li>
            );
          })}
        </ul>
      </section>
    ) : null;

  return (
    <>
      {group('Workspace', 'workspace', facets.workspace)}
      {group('List', 'list', facets.container)}
      {group('Type', 'contentType', facets.contentType)}
      {group('Tags', 'term', facets.term)}
    </>
  );
}
