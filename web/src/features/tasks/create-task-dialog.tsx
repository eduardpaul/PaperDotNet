import type { MyTask } from '@paperdotnet/client';
import { useMutation } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { rememberedList, rememberList, useListsByTemplate } from '@/features/lists/lists-by-template';
import { listBuilder } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';

/** A task in a task list, linked to this document (TSK-06). */
export function CreateTaskDialog({
  workspaceId,
  listId,
  itemId,
  title,
  open,
  onOpenChange,
}: {
  workspaceId: string;
  listId: string;
  itemId: string;
  title: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const navigate = useNavigate();
  const lists = useListsByTemplate('tasks');
  const [target, setTarget] = useState(
    () => rememberedList('taskFromDocument') ?? (lists.length === 1 ? lists[0]!.id! : ''),
  );
  const [name, setName] = useState(title);
  const create = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const list = lists.find((l) => l.id === target);
      if (!list) throw new Error('Choose a task list.');
      return (await listBuilder(workspaceId, listId)
        .items.byItemId(itemId)
        .tasks.post({ workspaceId: list.workspaceId, listId: list.id, title: name.trim() || title }))!;
    },
    onSuccess: (task: MyTask) => {
      rememberList('taskFromDocument', target);
      onOpenChange(false);
      toast.success('Task created.', {
        action: {
          label: 'Open',
          onClick: () =>
            void navigate({
              to: '/w/$workspaceId/l/$listId',
              params: { workspaceId: task.workspaceId!, listId: task.listId! },
              search: { item: task.itemId! },
            }),
        },
      });
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>Create a task</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <p className="text-[13px] text-muted">The task is linked to this document.</p>
            {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
            {!lists.length && <Alert>Create a task list first, then come back.</Alert>}
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="task-list">Task list</Label>
              <Select id="task-list" required value={target} onChange={(e) => setTarget(e.target.value)}>
                <option value="">Choose a task list…</option>
                {lists.map((list) => (
                  <option key={list.id} value={list.id!}>
                    {list.workspaceName ? `${list.workspaceName} › ` : ''}
                    {list.name}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="task-title">Title</Label>
              <Input id="task-title" required value={name} onChange={(e) => setName(e.target.value)} />
            </div>
          </div>
          <DialogFooter>
            <Button type="button" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!target || !name.trim() || create.isPending}>
              {create.isPending && <Spinner className="text-current" />} Create task
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
