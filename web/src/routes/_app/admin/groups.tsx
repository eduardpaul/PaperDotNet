import type { GroupResponse } from '@paperdotnet/client';
import { isStatus } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { ChevronRight, Pencil, Plus, Trash2, UserMinus, UserPlus, Users, X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Avatar } from '@/components/ui/avatar';
import { Button } from '@/components/ui/button';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Textarea } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Field } from '@/features/admin/common';
import { groupsQuery, userName, usersQuery } from '@/features/fields/directory';
import { useLibraries } from '@/features/lists/lists-by-template';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

export const Route = createFileRoute('/_app/admin/groups')({ component: Groups });

/** Groups of people (IAM-06) for permissions, approvals and notifications, with members and a shared inbox. */
function Groups() {
  const queryClient = useQueryClient();
  const { data: groups, isPending } = useQuery(groupsQuery);
  const [editing, setEditing] = useState<GroupResponse | 'new'>();
  const [deleting, setDeleting] = useState<GroupResponse>();
  const [open, setOpen] = useState<string>();
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (group: GroupResponse) => api.v10.groups.byId(group.id!).delete(),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('Group deleted.');
      await queryClient.invalidateQueries({ queryKey: groupsQuery.queryKey });
    },
  });
  return (
    <>
      <SettingsSection
        title="Groups"
        description="Give access, assign approvals and notify many people at once."
        className="px-0 pb-0"
        actions={
          <Button variant="primary" onClick={() => setEditing('new')}>
            <Plus /> New group
          </Button>
        }
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-24" />
        ) : groups?.length ? (
          <ul className="divide-y border-t">
            {groups.map((group) => (
              <li key={group.id}>
                <div className="flex items-center gap-3 px-5 py-2.5">
                  <button
                    type="button"
                    aria-expanded={open === group.id}
                    className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    onClick={() => setOpen(open === group.id ? undefined : group.id!)}
                  >
                    <ChevronRight
                      className={cn('size-4 text-muted transition-transform', open === group.id && 'rotate-90')}
                    />
                    <Users className="size-4 text-muted" />
                    <span className="min-w-0">
                      <span className="block truncate text-[13px] font-medium">{group.name}</span>
                      {group.description && (
                        <span className="block truncate text-xs text-muted">{group.description}</span>
                      )}
                    </span>
                  </button>
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`Edit ${group.name}`}
                    onClick={() => setEditing(group)}
                  >
                    <Pencil />
                  </Button>
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`Delete ${group.name}`}
                    onClick={() => setDeleting(group)}
                  >
                    <Trash2 />
                  </Button>
                </div>
                {open === group.id && <GroupDetails group={group} />}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState icon={Users} title="No groups yet" className="border-t py-8" />
        )}
      </SettingsSection>
      {editing && <GroupDialog group={editing === 'new' ? undefined : editing} onClose={() => setEditing(undefined)} />}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(o) => !o && setDeleting(undefined)}
        title={`Delete the group “${deleting?.name ?? ''}”?`}
        description="Its members lose what was granted to the group; approvals and automations naming it no longer reach anyone."
        confirm="Delete group"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  );
}

function GroupDetails({ group }: { group: GroupResponse }) {
  const queryClient = useQueryClient();
  const membersKey = ['groups', group.id, 'members'];
  const inboxKey = ['groups', group.id, 'inbox'];
  const { data: members } = useQuery({
    queryKey: membersKey,
    queryFn: async () => (await api.v10.groups.byId(group.id!).members.get()) ?? [],
  });
  const { data: inbox } = useQuery({
    queryKey: inboxKey,
    queryFn: async () => {
      try {
        return (await api.v10.groups.byId(group.id!).inbox.get()) ?? null;
      } catch (error) {
        if (isStatus(error, 404)) return null;
        throw error;
      }
    },
  });
  // Groups inside this group: their members are members too (ADR-0035).
  const { data: nested } = useQuery({
    queryKey: ['groups', group.id, 'groups'],
    queryFn: async () => (await api.v10.groups.byId(group.id!).groups.get()) ?? [],
  });
  const { data: users } = useQuery(usersQuery);
  const { data: groups } = useQuery(groupsQuery);
  const libraries = useLibraries();
  const [adding, setAdding] = useState<ComboboxOption[]>([]);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['groups', group.id] });
  const add = useMutation({
    mutationFn: async () => {
      for (const person of adding) await api.v10.groups.byId(group.id!).members.post({ userId: person.value });
    },
    onSuccess: async () => {
      setAdding([]);
      await refresh();
    },
  });
  const remove = useMutation({
    mutationFn: (userId: string) => api.v10.groups.byId(group.id!).members.byUserId(userId).delete(),
    onSuccess: refresh,
  });
  const nest = useMutation({
    meta: { silent: true },
    mutationFn: (groupId: string) => api.v10.groups.byId(group.id!).groups.post({ groupId }),
    onSuccess: refresh,
  });
  const unnest = useMutation({
    mutationFn: (groupId: string) => api.v10.groups.byId(group.id!).groups.byMemberGroupId(groupId).delete(),
    onSuccess: refresh,
  });
  const setInbox = useMutation({
    mutationFn: async (listId: string) => {
      const library = libraries.find((l) => l.id === listId);
      if (!library) await api.v10.groups.byId(group.id!).inbox.delete();
      else await api.v10.groups.byId(group.id!).inbox.put({ workspaceId: library.workspaceId, listId: library.id });
    },
    onSuccess: async () => {
      toast.success('Group inbox saved.');
      await refresh();
    },
  });
  const memberIds = new Set((members ?? []).map((m) => m.id));
  const options = (users ?? [])
    .filter((u) => !u.isDisabled && !memberIds.has(u.id))
    .map((u) => ({ value: u.id!, label: userName(u, u.id!), hint: u.email ?? u.userName ?? undefined }));
  return (
    <div className="flex flex-col gap-3 border-t bg-surface-muted/30 px-5 py-3 pl-12">
      <ul className="flex flex-col gap-1">
        {(members ?? []).map((member) => (
          <li key={member.id} className="flex items-center gap-2 text-[13px]">
            <Avatar name={userName(member, member.id!)} className="size-6" />
            <span className="flex-1">{userName(member, member.id!)}</span>
            <Button
              size="icon"
              variant="ghost"
              aria-label={`Remove ${userName(member, member.id!)} from ${group.name}`}
              onClick={() => remove.mutate(member.id!)}
            >
              <UserMinus />
            </Button>
          </li>
        ))}
        {members?.length === 0 && <li className="text-xs text-muted">No members yet.</li>}
      </ul>
      <div className="flex flex-wrap gap-2">
        <span id={`add-${group.id}`} className="sr-only">
          Add members to {group.name}
        </span>
        <div className="min-w-56 flex-1">
          <Combobox
            aria-labelledby={`add-${group.id}`}
            multiple
            selected={adding}
            options={options}
            placeholder="Add members…"
            onChange={setAdding}
          />
        </div>
        <Button disabled={!adding.length || add.isPending} onClick={() => add.mutate()}>
          <UserPlus /> Add
        </Button>
      </div>
      <Field id={`nested-${group.id}`} label="Groups inside" hint="Members of these groups get what this group gets.">
        <ul aria-label="Groups inside" className="flex flex-col gap-1">
          {(nested ?? []).map((inner) => (
            <li key={inner.id} className="flex items-center gap-2 text-[13px]">
              <Users className="size-4 text-muted" />
              <span className="flex-1">{inner.name}</span>
              <Button
                size="icon"
                variant="ghost"
                aria-label={`Take ${inner.name} out of ${group.name}`}
                onClick={() => unnest.mutate(inner.id!)}
              >
                <X />
              </Button>
            </li>
          ))}
        </ul>
        <Select
          id={`nested-${group.id}`}
          className="max-w-sm"
          value=""
          disabled={nest.isPending}
          onChange={(e) => e.target.value && nest.mutate(e.target.value)}
        >
          <option value="">Add a group…</option>
          {(groups ?? [])
            .filter((other) => other.id !== group.id && !nested?.some((inner) => inner.id === other.id))
            .map((other) => (
              <option key={other.id} value={other.id!}>
                {other.name}
              </option>
            ))}
        </Select>
      </Field>
      <Field
        id={`inbox-${group.id}`}
        label="Inbox"
        hint="Documents sent to the group (by upload or e-mail import) land in this library, shown under Inbox for its members."
      >
        <Select
          id={`inbox-${group.id}`}
          className="max-w-sm"
          value={inbox?.listId ?? ''}
          disabled={setInbox.isPending}
          onChange={(e) => setInbox.mutate(e.target.value)}
        >
          <option value="">No inbox</option>
          {libraries.map((l) => (
            <option key={l.id} value={l.id!}>
              {l.workspaceName} › {l.name}
            </option>
          ))}
        </Select>
      </Field>
      {(add.isError || nest.isError || setInbox.isError) && (
        <Alert>{problemMessage(add.error ?? nest.error ?? setInbox.error)}</Alert>
      )}
    </div>
  );
}

function GroupDialog({ group, onClose }: { group?: GroupResponse; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(group?.name ?? '');
  const [description, setDescription] = useState(group?.description ?? '');
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      group
        ? api.v10.groups.byId(group.id!).patch({ name: name.trim(), description: description.trim() })
        : api.v10.groups.post({ name: name.trim(), description: description.trim() || undefined }),
    onSuccess: async () => {
      toast.success(group ? 'Group saved.' : 'Group created.');
      await queryClient.invalidateQueries({ queryKey: groupsQuery.queryKey });
      onClose();
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{group ? `Edit ${group.name}` : 'New group'}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <Field id="group-name" label="Name">
              <Input id="group-name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
            </Field>
            <Field id="group-description" label="Description">
              <Textarea
                id="group-description"
                rows={2}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </Field>
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || save.isPending}>
              {group ? 'Save' : 'Create group'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
