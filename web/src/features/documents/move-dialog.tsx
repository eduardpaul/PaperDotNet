import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import { fieldsOf, ifMatch } from '@paperdotnet/client';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { listsQuery, workspacesQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { listBuilder } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';
import { fileVersionsQuery } from './queries';

/**
 * Files a document into another library (e.g. from the Inbox). The API has no cross-list move yet, so its pages are
 * copied into a new document there (DOC-06 extract; texts are carried over, nothing is OCRed again) with the same
 * title, and then the original goes to the recycle bin. If that last step fails, the user has a copy, never a loss.
 */
export function MoveDocumentDialog({
  workspaceId,
  list,
  item,
  open,
  onOpenChange,
  onMoved,
}: {
  workspaceId: string;
  list: ListResponse;
  item: ItemResponse;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onMoved?: () => void;
}) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { data: workspaces } = useQuery(workspacesQuery);
  const libraries = useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) })
    .flatMap((q) => q.data ?? [])
    .filter((l) => l.kind === 'library' && l.id !== list.id);
  const workspaceName = new Map((workspaces ?? []).map((w) => [w.id, w.isPersonal ? 'My files' : w.name]));
  const { data: versions } = useQuery({ ...fileVersionsQuery(workspaceId, list.id!, item.id!), enabled: open });
  const pageCount = versions?.find((v) => v.isCurrent)?.pageCount ?? 0;
  const [target, setTarget] = useState('');

  const move = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const library = libraries.find((l) => l.id === target)!;
      const source = listBuilder(workspaceId, list.id!).items.byItemId(item.id!);
      const result = await source.file.pages.extract.post({
        pages: Array.from({ length: pageCount }, (_, i) => i + 1),
        workspaceId: library.workspaceId,
        listId: library.id,
        title: String(fieldsOf(item).title ?? ''),
      });
      await source.delete(ifMatch(await source.get()));
      return { library, created: result?.documents?.[0] };
    },
    onSuccess: async ({ library, created }) => {
      onOpenChange(false);
      onMoved?.();
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, list.id!) }),
        queryClient.invalidateQueries({ queryKey: keys.items(library.workspaceId!, library.id!) }),
      ]);
      toast.success(`Moved to ${library.name}.`, {
        action: created
          ? {
              label: 'Open',
              onClick: () =>
                void navigate({
                  to: '/w/$workspaceId/l/$listId',
                  params: { workspaceId: library.workspaceId!, listId: library.id! },
                  search: { item: created.itemId! },
                }),
            }
          : undefined,
      });
    },
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    move.mutate();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>Move to a library</DialogTitle>
            <DialogDescription>
              The document and its text move; fields other than the title are set in the new library.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-5">
            {move.isError && <Alert>{problemMessage(move.error)}</Alert>}
            <Label htmlFor="move-target">Library</Label>
            <Select id="move-target" required value={target} onChange={(e) => setTarget(e.target.value)}>
              <option value="">Choose a library…</option>
              {libraries.map((l) => (
                <option key={l.id} value={l.id!}>
                  {workspaceName.get(l.workspaceId)} › {l.name}
                </option>
              ))}
            </Select>
          </div>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!target || !pageCount || move.isPending}>
              {move.isPending && <Spinner className="text-current" />} Move
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
