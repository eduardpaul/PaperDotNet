import type { ItemResponse, LocatedItemResponse, PageOfLocatedItemResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Link2, Plus, Unlink } from 'lucide-react';
import { useDeferredValue, useState } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { problemMessage } from '@/lib/errors';

/** Relationships are common to every content type and resolved by stable identity across workspaces. */
export function RelatedItems({ item, canWrite }: { item: ItemResponse; canWrite: boolean }) {
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const q = useDeferredValue(text.trim());
  const builder = api.v10.items.byItemId(item.id!).relations;
  const related = useInfiniteQuery({
    queryKey: keys.relations(item.id!),
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }): Promise<PageOfLocatedItemResponse> =>
      (await (pageParam ? builder.withUrl(pageParam).get() : builder.get({ queryParameters: { top: 50 } })))!,
    getNextPageParam: (page) => page.odataNextLink ?? undefined,
  });
  const search = useInfiniteQuery({
    queryKey: keys.globalItems(q, true),
    initialPageParam: undefined as string | undefined,
    enabled: canWrite && q.length > 0,
    queryFn: async ({ pageParam }): Promise<PageOfLocatedItemResponse> =>
      (await (pageParam
        ? api.v10.items.withUrl(pageParam).get()
        : api.v10.items.get({ queryParameters: { q, writable: true, top: 20 } })))!,
    getNextPageParam: (page) => page.odataNextLink ?? undefined,
  });
  const entries = related.data?.pages.flatMap((p) => p.value ?? []) ?? [];
  const relatedIds = new Set(entries.map((r) => r.item?.id));
  const matches = (search.data?.pages.flatMap((p) => p.value ?? []) ?? []).filter(
    (r) => r.item?.id !== item.id && !relatedIds.has(r.item?.id),
  );
  const change = useMutation({
    mutationFn: ({ otherId, add }: { otherId: string; add: boolean }) =>
      add ? builder.byOtherId(otherId).put() : builder.byOtherId(otherId).delete(),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.globalItemResources }),
        queryClient.invalidateQueries({ queryKey: keys.workspaces }),
      ]);
    },
  });

  return (
    <div className="flex flex-col gap-4 p-5">
      <p className="text-[13px] text-muted">Linked items appear on both sides and stay connected when moved.</p>
      {related.isError && <Alert>{problemMessage(related.error)}</Alert>}
      {change.isError && <Alert>{problemMessage(change.error)}</Alert>}
      {related.isPending ? (
        <Skeleton className="h-12" />
      ) : entries.length ? (
        <ul aria-label="Related items" className="flex flex-col gap-2">
          {entries.map((entry) => (
            <li key={entry.item?.id} className="flex items-center gap-2 rounded-md border p-2">
              <Link2 className="size-4 shrink-0 text-muted" />
              <Link to="/i/$itemId" params={{ itemId: entry.item!.id! }} className="min-w-0 flex-1">
                <ItemLabel entry={entry} />
              </Link>
              {canWrite && entry.canRelate && (
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={`Unlink ${fieldsOf(entry.item).title}`}
                  disabled={change.isPending}
                  onClick={() => change.mutate({ otherId: entry.item!.id!, add: false })}
                >
                  <Unlink />
                </Button>
              )}
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-sm text-muted">No related items you can access.</p>
      )}
      {related.hasNextPage && (
        <Button disabled={related.isFetchingNextPage} onClick={() => void related.fetchNextPage()}>
          More related items
        </Button>
      )}
      {canWrite && (
        <section className="flex flex-col gap-2">
          <Label htmlFor="related-search">Link another item</Label>
          <Input
            id="related-search"
            placeholder="Search titles across workspaces…"
            value={text}
            onChange={(event) => setText(event.target.value)}
          />
          <p className="text-xs text-muted">
            Choose an item you can edit. Documents, tasks, events and notes can all be linked.
          </p>
          {q && search.isPending && <Spinner />}
          {search.isError && <Alert>{problemMessage(search.error)}</Alert>}
          {q && search.isSuccess && matches.length === 0 && <p className="text-sm text-muted">No matching items.</p>}
          {matches.length > 0 && (
            <ul aria-label="Items to link" className="flex flex-col gap-1">
              {matches.map((entry) => (
                <li key={entry.item?.id}>
                  <button
                    type="button"
                    className="flex w-full items-center gap-2 rounded-md px-2 py-2 text-left hover:bg-surface-muted disabled:opacity-50"
                    disabled={change.isPending}
                    onClick={() => change.mutate({ otherId: entry.item!.id!, add: true })}
                  >
                    <Plus className="size-4 shrink-0" />
                    <ItemLabel entry={entry} />
                  </button>
                </li>
              ))}
            </ul>
          )}
          {search.hasNextPage && (
            <Button disabled={search.isFetchingNextPage} onClick={() => void search.fetchNextPage()}>
              More matches
            </Button>
          )}
        </section>
      )}
      <Link to="/i/$itemId" params={{ itemId: item.id! }} className="text-xs text-accent">
        Permanent link to this item
      </Link>
    </div>
  );
}

function ItemLabel({ entry }: { entry: LocatedItemResponse }) {
  return (
    <span className="block min-w-0">
      <span className="block truncate text-sm font-medium">{String(fieldsOf(entry.item).title ?? 'Untitled')}</span>
      <span className="block truncate text-xs text-muted">
        {entry.contentTypeName} · {entry.workspaceName} › {entry.listName}
      </span>
    </span>
  );
}
