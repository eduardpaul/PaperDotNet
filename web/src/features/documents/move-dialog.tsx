import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import { fields as fieldValues, fieldsOf, ifMatch } from '@paperdotnet/client';
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
import { listFields } from '@/features/lists/schema';
import { problemMessage } from '@/lib/errors';
import { fileVersionsQuery } from './queries';

/**
 * Files a document into another library. There is no cross-list move, so the pages are copied (text included, no new
 * OCR) and the fields the target library has are copied onto the new item. Versions, comments and links stay on the
 * original, which then goes to the recycle bin. If that last step fails, the user has a copy, never a loss.
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
      const created = result?.documents?.[0];
      if (created?.itemId) {
        const targetList = await listBuilder(library.workspaceId!, library.id!).get();
        const allowed = new Set(listFields(targetList).map((field) => field.name));
        const patch = Object.fromEntries(
          Object.entries(fieldsOf(item)).filter(
            ([name, value]) => name !== 'title' && allowed.has(name) && value !== undefined && value !== null,
          ),
        );
        if (Object.keys(patch).length) {
          await listBuilder(created.workspaceId!, created.listId!)
            .items.byItemId(created.itemId)
            .patch({ fields: fieldValues(patch) });
        }
      }
      await source.delete(ifMatch(await source.get()));
      return { library, created };
    },
    onSuccess: async ({ library, created }) => {
      onOpenChange(false);
      onMoved?.();
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, list.id!) }),
        queryClient.invalidateQueries({ queryKey: keys.items(library.workspaceId!, library.id!) }),
      ]);
      toast.success(`Filed in ${library.name}.`, {
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
            <DialogTitle>File in a library</DialogTitle>
            <DialogDescription>
              The file is copied, and so are the fields this library has. Versions, comments and links stay on the
              original, which then goes to the recycle bin.
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
              {move.isPending && <Spinner className="text-current" />} File
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
