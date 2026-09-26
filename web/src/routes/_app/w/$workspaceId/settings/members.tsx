import type { WorkspaceMemberResponse, WorkspaceRole } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { UserPlus, UserX } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { meQuery } from '@/api/queries';
import { Avatar } from '@/components/ui/avatar';
import { Button } from '@/components/ui/button';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Select } from '@/components/ui/select';
import { userName, usersQuery, useUsers } from '@/features/fields/directory';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { membersQuery, workspaceBuilder, workspaceQuery } from '@/features/workspaces/queries';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/settings/members')({ component: Members });

const roles: { value: WorkspaceRole; label: string; description: string }[] = [
  { value: 'owner', label: 'Owner', description: 'Changes settings, members and automations' },
  { value: 'member', label: 'Member', description: 'Adds and edits items and documents' },
  { value: 'visitor', label: 'Visitor', description: 'Reads everything, changes nothing' },
];

/** Who belongs to the workspace, with which role (PLT-07, IAM-07). */
function Members() {
  const { workspaceId } = Route.useParams();
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const { data: me } = useQuery(meQuery);
  const { data: members, isPending } = useQuery(membersQuery(workspaceId));
  const users = useUsers();
  const [removing, setRemoving] = useState<WorkspaceMemberResponse>();
  const canManage = workspace?.access === 'manage';
  const invalidate = () => queryClient.invalidateQueries({ queryKey: membersQuery(workspaceId).queryKey });
  const setRole = useMutation({
    mutationFn: (member: { userId: string; role: WorkspaceRole }) => workspaceBuilder(workspaceId).members.post(member),
    onSuccess: invalidate,
    onError: invalidate,
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (member: WorkspaceMemberResponse) =>
      workspaceBuilder(workspaceId).members.byUserId(member.userId!).delete(),
    onSuccess: async () => {
      setRemoving(undefined);
      toast.success('Member removed.');
      await invalidate();
    },
  });
  const nameOf = (id: string) => userName(users.get(id), id);
  const sorted = [...(members ?? [])].sort((a, b) => nameOf(a.userId!).localeCompare(nameOf(b.userId!)));

  if (workspace?.isPersonal)
    return (
      <SettingsSection title="Members" description="A personal workspace belongs to you alone; it has no members." />
    );
  return (
    <>
      {canManage && <AddMember workspaceId={workspaceId} existing={(members ?? []).map((m) => m.userId!)} />}
      <SettingsSection
        title="Members"
        description={`${members?.length ?? 0} ${members?.length === 1 ? 'person' : 'people'}. Administrators can open every workspace.`}
        className="px-0 pb-0"
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-24" />
        ) : (
          <ul className="divide-y border-t">
            {sorted.map((member) => {
              const user = users.get(member.userId!);
              const name = nameOf(member.userId!);
              return (
                <li key={member.userId} className="flex flex-wrap items-center gap-3 px-5 py-2.5">
                  <Avatar name={name} className="size-8" />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-[13px] font-medium">
                      {name}
                      {member.userId === me?.id && <span className="font-normal text-muted"> (you)</span>}
                    </p>
                    <p className="truncate text-xs text-muted">{user?.email ?? user?.userName}</p>
                  </div>
                  <Select
                    aria-label={`Role of ${name}`}
                    className="w-32"
                    value={member.role ?? 'member'}
                    disabled={!canManage || setRole.isPending}
                    onChange={(e) => setRole.mutate({ userId: member.userId!, role: e.target.value as WorkspaceRole })}
                  >
                    {roles.map((r) => (
                      <option key={r.value} value={r.value}>
                        {r.label}
                      </option>
                    ))}
                  </Select>
                  {canManage && (
                    <Button
                      variant="ghost"
                      size="icon"
                      aria-label={`Remove ${name}`}
                      onClick={() => setRemoving(member)}
                    >
                      <UserX />
                    </Button>
                  )}
                </li>
              );
            })}
          </ul>
        )}
        {setRole.isError && <Alert className="m-5">{problemMessage(setRole.error)}</Alert>}
      </SettingsSection>
      <ul className="grid gap-2 text-xs text-muted sm:grid-cols-3">
        {roles.map((r) => (
          <li key={r.value}>
            <span className="font-medium text-foreground">{r.label}:</span> {r.description}
          </li>
        ))}
      </ul>
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(open) => !open && setRemoving(undefined)}
        title="Remove from the workspace?"
        description={`${removing ? nameOf(removing.userId!) : ''} will no longer see this workspace, unless a list or item is shared with them.`}
        confirm="Remove"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </>
  );
}

function AddMember({ workspaceId, existing }: { workspaceId: string; existing: string[] }) {
  const queryClient = useQueryClient();
  const { data: users } = useQuery(usersQuery);
  const [people, setPeople] = useState<ComboboxOption[]>([]);
  const [role, setRole] = useState<WorkspaceRole>('member');
  const add = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      for (const person of people) await workspaceBuilder(workspaceId).members.post({ userId: person.value, role });
    },
    onSuccess: async () => {
      toast.success(people.length === 1 ? `${people[0]!.label} added.` : `${people.length} people added.`);
      setPeople([]);
      await queryClient.invalidateQueries({ queryKey: membersQuery(workspaceId).queryKey });
    },
  });
  const options = (users ?? [])
    .filter((u) => !u.isDisabled && !existing.includes(u.id!))
    .map((u) => ({ value: u.id!, label: userName(u, u.id!), hint: u.email ?? u.userName ?? undefined }));
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (people.length) add.mutate();
  };
  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Add people"
        actions={
          <Button type="submit" variant="primary" disabled={!people.length || add.isPending}>
            <UserPlus /> Add
          </Button>
        }
      >
        <div className="flex flex-col gap-2 sm:flex-row">
          <span id="add-people-label" className="sr-only">
            People
          </span>
          <div className="min-w-0 flex-1">
            <Combobox
              aria-labelledby="add-people-label"
              multiple
              selected={people}
              options={options}
              placeholder="Choose people…"
              onChange={setPeople}
            />
          </div>
          <Select
            aria-label="Role"
            className="sm:w-36"
            value={role}
            onChange={(e) => setRole(e.target.value as WorkspaceRole)}
          >
            {roles.map((r) => (
              <option key={r.value} value={r.value}>
                {r.label}
              </option>
            ))}
          </Select>
        </div>
        {add.isError && <Alert className="mt-3">{problemMessage(add.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}
