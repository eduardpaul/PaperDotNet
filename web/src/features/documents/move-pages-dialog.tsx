import type { ItemResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { listBuilder, odataString } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';

/** Moves the selected pages into another document of this library (DOC-06). All pages merges and recycles the source. */
export function MovePagesDialog({
  workspaceId,
  listId,
  itemId,
  pages,
  open,
  onOpenChange,
  onMoved,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  pages: number[];
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onMoved: () => void;
}) {
  const queryClient = useQueryClient();
  const [query, setQuery] = useState('');
  const [target, setTarget] = useState('');
  const documents = useQuery({
    queryKey: ['workspaces', workspaceId, 'lists', listId, 'move-pages', query],
    enabled: open,
    queryFn: async () =>
      (
        await listBuilder(workspaceId, listId).items.get({
          queryParameters: {
            filter: `isFolder eq false and id ne ${itemId}${
              query.trim() ? ` and contains(tolower(fields/title),${odataString(query.trim().toLowerCase())})` : ''
            }`,
            select: 'title',
            top: 20,
          },
        })
      )?.value ?? [],
  });
  const move = useMutation({
    meta: { silent: true },
    mutationFn: (document: ItemResponse) =>
      listBuilder(workspaceId, listId).items.byItemId(itemId).file.pages.move.post({
        targetWorkspaceId: workspaceId,
        targetListId: listId,
        targetItemId: document.id,
        pages,
        position: 'append',
      }),
    onSuccess: async (result) => {
      onOpenChange(false);
      onMoved();
      toast.success(result?.sourceDeleted ? 'Pages moved. This document is in the recycle bin.' : 'Pages moved.');
      await queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) });
    },
  });
  const chosen = documents.data?.find((d) => d.id === target);
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (chosen) move.mutate(chosen);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>Move pages into a document</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <p className="text-[13px] text-muted">
              {pages.length} {pages.length === 1 ? 'page is' : 'pages are'} appended. Moving every page puts this
              document in the recycle bin.
            </p>
            {move.isError && <Alert>{problemMessage(move.error)}</Alert>}
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="move-pages-search">Document</Label>
              <Input
                id="move-pages-search"
                placeholder="Search titles…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
              />
            </div>
            <div className="flex max-h-48 flex-col overflow-y-auto rounded-md border">
              {(documents.data ?? []).map((document) => (
                <label
                  key={document.id}
                  className="flex items-center gap-2 border-b px-3 py-2 text-[13px] last:border-b-0"
                >
                  <input
                    type="radio"
                    name="move-pages-target"
                    value={document.id ?? ''}
                    checked={target === document.id}
                    onChange={() => setTarget(document.id!)}
                  />
                  <span className="truncate">{String(fieldsOf(document).title ?? 'Untitled')}</span>
                </label>
              ))}
              {!documents.isPending && !documents.data?.length && (
                <p className="px-3 py-4 text-[13px] text-muted">No other documents in this library.</p>
              )}
            </div>
          </div>
          <DialogFooter>
            <Button type="button" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!chosen || move.isPending}>
              {move.isPending && <Spinner className="text-current" />} Move pages
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
