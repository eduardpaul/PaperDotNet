import type { ItemResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Alert } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { problemMessage } from '@/lib/errors';
import { smartFoldersQuery } from './queries';

/** Classifies an item by putting it into a smart folder (TAX-09): the folder's tags and values are applied. */
export function AddToFolderDialog({
  workspaceId,
  listId,
  item,
  open,
  onOpenChange,
}: {
  workspaceId: string;
  listId: string;
  item: ItemResponse;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const { data: folders } = useQuery(smartFoldersQuery);
  const [folderId, setFolderId] = useState('');
  const add = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.smartFolders.byId(folderId).items.post({ workspaceId, listId, itemId: item.id }),
    onSuccess: async () => {
      toast.success(`Added to ${folders?.find((f) => f.id === folderId)?.name}.`);
      onOpenChange(false);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: [...keys.smartFolders, folderId] }),
        queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) }),
      ]);
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate();
  };
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>Add to a smart folder</DialogTitle>
            <DialogDescription>
              “{String(fieldsOf(item).title ?? '')}” gets the folder’s tags and values.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-5">
            {add.isError && <Alert>{problemMessage(add.error)}</Alert>}
            <Label htmlFor="add-folder">Smart folder</Label>
            <Select id="add-folder" required value={folderId} onChange={(e) => setFolderId(e.target.value)}>
              <option value="">Choose a folder…</option>
              {(folders ?? []).map((f) => (
                <option key={f.id} value={f.id!}>
                  {f.name}
                </option>
              ))}
            </Select>
          </div>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!folderId || add.isPending}>
              Add
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
