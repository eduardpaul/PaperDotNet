import type { ListTemplateResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
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
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input, Label, Textarea } from '@/components/ui/input';
import { ListIcon } from '@/features/lists/list-icon';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

/** A new list or library from a template (LST-16): Documents, Tasks, Calendar, Contacts, Notes, or extension ones. */
export function NewListDialog({
  workspaceId,
  open,
  onOpenChange,
}: {
  workspaceId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { data: templates, isPending } = useQuery({
    queryKey: ['listTemplates'],
    queryFn: async () => (await api.v10.listTemplates.get()) ?? [],
    staleTime: 5 * 60_000,
    enabled: open,
  });
  const [template, setTemplate] = useState<ListTemplateResponse>();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const create = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      api.v10.workspaces.byWorkspaceId(workspaceId).lists.post({
        name: name.trim(),
        description: description.trim() || undefined,
        templateKey: template?.key ?? undefined,
        kind: template?.isLibrary ? 'library' : 'list',
      }),
    onSuccess: async (list) => {
      await queryClient.invalidateQueries({ queryKey: keys.lists(workspaceId) });
      onOpenChange(false);
      await navigate({ to: '/w/$workspaceId/l/$listId', params: { workspaceId, listId: list!.id! } });
    },
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl">
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>New list or library</DialogTitle>
            <DialogDescription>Start from a template; fields and views can be changed later.</DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-4 px-5 pb-5">
            {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
            <div role="radiogroup" aria-label="Template" className="grid gap-2 sm:grid-cols-3">
              {isPending
                ? [0, 1, 2].map((i) => <Skeleton key={i} className="h-20" />)
                : [undefined, ...(templates ?? [])].map((t) => (
                    <button
                      key={t?.key ?? 'blank'}
                      type="button"
                      role="radio"
                      aria-checked={template?.key === t?.key}
                      onClick={() => {
                        setTemplate(t);
                        if (!name || name === template?.name) setName(t?.name ?? '');
                      }}
                      className={cn(
                        'flex flex-col items-start gap-1 rounded-lg border p-3 text-left hover:border-accent/50',
                        template?.key === t?.key && 'border-accent bg-accent-soft/40',
                      )}
                    >
                      <span className="flex items-center gap-2 text-[13px] font-semibold">
                        <ListIcon
                          list={{ templateKey: t?.key, kind: t?.isLibrary ? 'library' : 'list' }}
                          className="size-4 text-accent"
                        />
                        {t?.name ?? 'Blank list'}
                      </span>
                      <span className="line-clamp-2 text-xs text-muted">
                        {t?.description ?? 'Just a title; add your own fields.'}
                      </span>
                    </button>
                  ))}
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="list-name">Name</Label>
              <Input id="list-name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="list-description">Description</Label>
              <Textarea
                id="list-description"
                rows={2}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </div>
          </div>
          <DialogFooter>
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || create.isPending}>
              Create
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
