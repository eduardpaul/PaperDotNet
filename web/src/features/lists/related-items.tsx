import type {
  ItemResponse,
  LocatedItemResponse,
  PageOfLocatedItemResponse,
  PageOfItemRelationshipResponse,
  RelationshipTypeData,
} from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Link2, Plus, Unlink } from 'lucide-react';
import { useDeferredValue, useState } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Combobox } from '@/components/ui/combobox';
import { Select } from '@/components/ui/select';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert, Skeleton, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { problemMessage } from '@/lib/errors';

/** Relationships are common to every content type and resolved by stable identity across workspaces. */
export function RelatedItems({ item, canWrite }: { item: ItemResponse; canWrite: boolean }) {
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const q = useDeferredValue(text.trim());
  const builder = api.v10.items.byItemId(item.id!).relationships;
  const [typeId, setTypeId] = useState('');
  const [direction, setDirection] = useState('symmetric');
  const [filterType, setFilterType] = useState('');
  const [filterDirection, setFilterDirection] = useState('both');
  const [newType, setNewType] = useState<string | null>(null);
  const [newDirected, setNewDirected] = useState(false);
  const [inverse, setInverse] = useState('');
  const [maxIncoming, setMaxIncoming] = useState('');
  const [maxOutgoing, setMaxOutgoing] = useState('');
  const types = useQuery({
    queryKey: [...keys.globalItemResources, 'types'],
    queryFn: async () => (await api.v10.relationshipTypes.get()) ?? [],
  });
  const selectedType = types.data?.find((t) => t.id === typeId);
  const createType = useMutation({
    mutationFn: () =>
      api.v10.relationshipTypes.post({
        name: newType!,
        directed: newDirected,
        inverseLabel: newDirected ? inverse || undefined : undefined,
        maxIncoming: newDirected && maxIncoming ? Number(maxIncoming) : undefined,
        maxOutgoing: newDirected && maxOutgoing ? Number(maxOutgoing) : undefined,
      }),
    onSuccess: async (type) => {
      setTypeId(type!.id!);
      setDirection(type?.directed ? 'outgoing' : 'symmetric');
      setNewType(null);
      await queryClient.invalidateQueries({ queryKey: keys.globalItemResources });
    },
  });
  const related = useInfiniteQuery({
    queryKey: [...keys.relations(item.id!), filterType, filterDirection],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }): Promise<PageOfItemRelationshipResponse> =>
      (await (pageParam
        ? builder.withUrl(pageParam).get()
        : builder.get({ queryParameters: { top: 50, type: filterType || undefined, direction: filterDirection } })))!,
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
  const matches = (search.data?.pages.flatMap((p) => p.value ?? []) ?? []).filter((r) => r.item?.id !== item.id);
  const change = useMutation({
    mutationFn: ({ otherId, add }: { otherId: string; add: boolean }) => {
      if (!add) return builder.byRelationshipId(otherId).delete();
      const source = direction === 'incoming' ? api.v10.items.byItemId(otherId).relationships : builder;
      return source.post({
        otherId: direction === 'incoming' ? item.id : otherId,
        type: typeId || undefined,
        directed: direction !== 'symmetric',
      });
    },
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
      <div className="grid grid-cols-2 gap-2">
        <div>
          <Label htmlFor="relationship-filter">Filter by type</Label>
          <Select id="relationship-filter" value={filterType} onChange={(event) => setFilterType(event.target.value)}>
            <option value="">All types</option>
            {types.data?.map((type) => (
              <option key={type.id} value={type.id!}>
                {type.name}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <Label htmlFor="relationship-direction-filter">Filter by direction</Label>
          <Select
            id="relationship-direction-filter"
            value={filterDirection}
            onChange={(event) => setFilterDirection(event.target.value)}
          >
            <option value="both">Both directions</option>
            <option value="incoming">Incoming</option>
            <option value="outgoing">Outgoing</option>
          </Select>
        </div>
      </div>
      {types.isError && <Alert>{problemMessage(types.error)}</Alert>}
      {related.isError && <Alert>{problemMessage(related.error)}</Alert>}
      {change.isError && <Alert>{problemMessage(change.error)}</Alert>}
      {related.isPending ? (
        <Skeleton className="h-12" />
      ) : entries.length ? (
        <ul aria-label="Related items" className="flex flex-col gap-2">
          {entries.map((entry) => (
            <li key={entry.id} className="flex items-center gap-2 rounded-md border p-2">
              <Link2 className="size-4 shrink-0 text-muted" />
              <Link to="/i/$itemId" params={{ itemId: entry.relatedItem!.item!.id! }} className="min-w-0 flex-1">
                <span className="block text-xs text-muted">
                  {edgeLabel(entry.type, entry.directed ?? false, entry.sourceItemId === item.id)}
                </span>
                <ItemLabel entry={entry.relatedItem!} />
              </Link>
              {canWrite && entry.relatedItem?.canRelate && (
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={`Unlink ${fieldsOf(entry.relatedItem?.item).title}`}
                  disabled={change.isPending}
                  onClick={() => change.mutate({ otherId: entry.id!, add: false })}
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
          <Label htmlFor="relationship-type" id="relationship-type-label">
            Relationship type (optional)
          </Label>
          <Combobox
            id="relationship-type"
            aria-labelledby="relationship-type-label"
            selected={typeId ? [{ value: typeId, label: selectedType?.name ?? '…' }] : []}
            options={(types.data ?? []).map((type) => ({ value: type.id!, label: type.name ?? '' }))}
            placeholder="Related (untyped)"
            onChange={(options) => {
              const type = types.data?.find((t) => t.id === options[0]?.value);
              setTypeId(type?.id ?? '');
              setDirection(type?.directed ? 'outgoing' : 'symmetric');
            }}
            onCreate={(name) => {
              setNewType(name);
              setNewDirected(direction !== 'symmetric');
              setInverse('');
              setMaxIncoming('');
              setMaxOutgoing('');
              createType.reset();
            }}
          />
          <Label htmlFor="relationship-direction">Relationship direction</Label>
          <Select id="relationship-direction" value={direction} onChange={(event) => setDirection(event.target.value)}>
            {(!selectedType || !selectedType.directed) && (
              <option value="symmetric">Both items are equally related</option>
            )}
            {(!selectedType || selectedType.directed) && (
              <>
                <option value="outgoing">This item → other item</option>
                <option value="incoming">Other item → this item</option>
              </>
            )}
          </Select>
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
      <Dialog
        open={newType !== null}
        onOpenChange={(open) => {
          if (!open) setNewType(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create relationship type</DialogTitle>
            <DialogDescription>
              Types use the shared term store. Their direction and limits stay fixed once created.
            </DialogDescription>
          </DialogHeader>
          <Label htmlFor="new-relationship-name">Name</Label>
          <Input
            id="new-relationship-name"
            value={newType ?? ''}
            onChange={(event) => setNewType(event.target.value)}
            maxLength={256}
          />
          <Label htmlFor="new-relationship-direction">Direction</Label>
          <Select
            id="new-relationship-direction"
            value={newDirected ? 'outgoing' : 'symmetric'}
            onChange={(event) => setNewDirected(event.target.value === 'outgoing')}
          >
            <option value="symmetric">Symmetric</option>
            <option value="outgoing">Directed</option>
          </Select>
          {newDirected && (
            <>
              <Label htmlFor="relationship-inverse">Inverse label (optional)</Label>
              <Input
                id="relationship-inverse"
                value={inverse}
                onChange={(event) => setInverse(event.target.value)}
                maxLength={256}
                placeholder="e.g. belongs to receipt"
              />
              <Label htmlFor="relationship-max-incoming">Maximum incoming links per item (optional)</Label>
              <Input
                id="relationship-max-incoming"
                type="number"
                min={1}
                max={1000}
                value={maxIncoming}
                onChange={(event) => setMaxIncoming(event.target.value)}
              />
              <Label htmlFor="relationship-max-outgoing">Maximum outgoing links per item (optional)</Label>
              <Input
                id="relationship-max-outgoing"
                type="number"
                min={1}
                max={1000}
                value={maxOutgoing}
                onChange={(event) => setMaxOutgoing(event.target.value)}
              />
            </>
          )}
          {createType.isError && <Alert>{problemMessage(createType.error)}</Alert>}
          <DialogFooter>
            <Button disabled={!newType?.trim() || createType.isPending} onClick={() => createType.mutate()}>
              Create type
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
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

function edgeLabel(type: RelationshipTypeData | null | undefined, directed: boolean, outgoing: boolean) {
  const label = type?.name ?? 'Related';
  return !directed ? label : outgoing ? `→ ${label}` : `← ${type?.inverseLabel ?? label}`;
}
