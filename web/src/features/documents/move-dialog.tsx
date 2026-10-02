import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
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

/** Files a document into a compatible library while preserving its identity, versions, comments and links. */
export function MoveItemDialog({
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
  const isLibrary = list.kind === 'library';
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { data: workspaces } = useQuery(workspacesQuery);
  const libraries = useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) })
    .flatMap((q) => q.data ?? [])
    .filter((l) => l.kind === list.kind && l.id !== list.id);
  const workspaceName = new Map((workspaces ?? []).map((w) => [w.id, w.isPersonal ? 'My files' : w.name]));
  const [target, setTarget] = useState('');

  const move = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const library = libraries.find((l) => l.id === target)!;
      const source = listBuilder(workspaceId, list.id!).items.byItemId(item.id!);
      const moved = await api.v10.items
        .byItemId(item.id!)
        .move.post({ workspaceId: library.workspaceId, listId: library.id }, ifMatch(await source.get()));
      return { library, moved };
    },
    onSuccess: async ({ library, moved }) => {
      onOpenChange(false);
      onMoved?.();
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, list.id!) }),
        queryClient.invalidateQueries({ queryKey: keys.items(library.workspaceId!, library.id!) }),
        queryClient.invalidateQueries({ queryKey: keys.globalItemResources }),
      ]);
      toast.success(`${isLibrary ? 'Filed in' : 'Moved to'} ${library.name}.`, {
        action: moved
          ? {
              label: 'Open',
              onClick: () =>
                void navigate({
                  to: '/w/$workspaceId/l/$listId',
                  params: { workspaceId: library.workspaceId!, listId: library.id! },
                  search: { item: moved.item!.id! },
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
            <DialogTitle>{isLibrary ? 'File in a library' : 'Move to a list'}</DialogTitle>
            <DialogDescription>
              Versions, comments and links move with the item. The destination must support its content type. Access
              will follow the destination.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-5">
            {move.isError && <Alert>{problemMessage(move.error)}</Alert>}
            <Label htmlFor="move-target">{isLibrary ? 'Library' : 'List'}</Label>
            <Select id="move-target" required value={target} onChange={(e) => setTarget(e.target.value)}>
              <option value="">{isLibrary ? 'Choose a library…' : 'Choose a list…'}</option>
              {libraries.map((l) => (
                <option key={l.id} value={l.id!}>
                  {workspaceName.get(l.workspaceId)} › {l.name}
                </option>
              ))}
            </Select>
          </div>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!target || move.isPending}>
              {move.isPending && <Spinner className="text-current" />} {isLibrary ? 'File' : 'Move'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
