import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { useAdminAccess } from '@/features/admin/access';
import { groupsQuery, usersQuery } from '@/features/fields/directory';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

interface PeopleSearch {
  section?: 'groups' | 'roles';
}

export const Route = createFileRoute('/_app/admin/')({
  validateSearch: (search: Record<string, unknown>): PeopleSearch => ({
    section: search.section === 'groups' || search.section === 'roles' ? search.section : undefined,
  }),
  component: People,
});

function People() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const admin = useAdminAccess();
  const section = search.section ?? 'users';
  const tabs = [
    admin.has('user.manage') ? { key: undefined, label: 'Users' } : undefined,
    admin.has('group.manage') ? { key: 'groups' as const, label: 'Groups' } : undefined,
    admin.has('role.manage') ? { key: 'roles' as const, label: 'Roles' } : undefined,
  ].filter((tab) => tab !== undefined);

  return (
    <>
      {tabs.length > 1 && (
        <div role="tablist" aria-label="People" className="inline-flex rounded-md bg-surface-muted p-0.5">
          {tabs.map((tab) => (
            <button
              key={tab.label}
              type="button"
              role="tab"
              aria-selected={section === (tab.key ?? 'users')}
              className={cn(
                'rounded px-2.5 py-1 text-xs font-medium text-muted',
                section === (tab.key ?? 'users') && 'bg-surface text-foreground shadow-xs',
              )}
              onClick={() => void navigate({ search: { section: tab.key } })}
            >
              {tab.label}
            </button>
          ))}
        </div>
      )}
      {section === 'groups' ? <Groups /> : section === 'roles' ? <Roles /> : <Users />}
    </>
  );
}

function Users() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(usersQuery);
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [resetFor, setResetFor] = useState<string>();
  const [nextPassword, setNextPassword] = useState('');
  const create = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      api.v10.users.post({
        userName: userName.trim(),
        password,
        displayName: displayName.trim() || undefined,
        email: email.trim() || undefined,
      }),
    onSuccess: async () => {
      setUserName('');
      setPassword('');
      setDisplayName('');
      setEmail('');
      toast.success('User created.');
      await queryClient.invalidateQueries({ queryKey: usersQuery.queryKey });
    },
  });
  const toggle = useMutation({
    mutationFn: (user: { id: string; isDisabled?: boolean | null }) =>
      api.v10.users.byId(user.id).patch({ isDisabled: !user.isDisabled }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersQuery.queryKey }),
  });
  const reset = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.users.byId(resetFor!).password.post({ password: nextPassword }),
    onSuccess: () => {
      setResetFor(undefined);
      setNextPassword('');
      toast.success('Password set. Their other sessions are signed out.');
    },
  });
  const [remove, setRemove] = useState<{ id: string; name: string }>();
  const removeUser = useMutation({
    meta: { silent: true },
    mutationFn: (id: string) => api.v10.users.byId(id).delete(),
    onSuccess: async () => {
      setRemove(undefined);
      toast.success('User deleted. Their user name can be used again.');
      await queryClient.invalidateQueries({ queryKey: usersQuery.queryKey });
    },
  });

  return (
    <>
      <SettingsSection title="Users" description="Create an account, turn one off, or set a new password.">
        {isPending ? (
          <Skeleton className="h-16" />
        ) : (
          <ul className="divide-y">
            {(data ?? []).map((user) => (
              <li key={user.id} className="flex flex-wrap items-center gap-2 py-2 text-[13px]">
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{user.displayName || user.userName}</span>
                  <span className="ml-2 text-muted">{user.userName}</span>
                  {user.email && <span className="ml-2 text-muted">{user.email}</span>}
                  {user.isDisabled && <span className="ml-2 text-danger">Disabled</span>}
                </span>
                <Button size="sm" onClick={() => setResetFor(user.id!)}>
                  Set password
                </Button>
                <Button size="sm" onClick={() => toggle.mutate({ id: user.id!, isDisabled: user.isDisabled })}>
                  {user.isDisabled ? 'Enable' : 'Disable'}
                </Button>
                <Button
                  size="sm"
                  onClick={() => setRemove({ id: user.id!, name: user.displayName || user.userName || 'this user' })}
                >
                  Delete
                </Button>
              </li>
            ))}
          </ul>
        )}
      </SettingsSection>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          create.mutate();
        }}
      >
        <SettingsSection
          title="New user"
          actions={
            <Button
              type="submit"
              variant="primary"
              disabled={!userName.trim() || password.length < 1 || create.isPending}
            >
              Create user
            </Button>
          }
        >
          {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
          <div className="grid gap-3 sm:grid-cols-2">
            <Field id="new-user-name" label="User name" value={userName} onChange={setUserName} required />
            <Field
              id="new-user-password"
              label="Initial password"
              type="password"
              value={password}
              onChange={setPassword}
              required
            />
            <Field label="Display name" value={displayName} onChange={setDisplayName} />
            <Field label="Email" type="email" value={email} onChange={setEmail} />
          </div>
        </SettingsSection>
      </form>
      {resetFor && (
        <form
          onSubmit={(event: FormEvent) => {
            event.preventDefault();
            reset.mutate();
          }}
        >
          <SettingsSection
            title="New password"
            actions={
              <>
                <Button type="button" onClick={() => setResetFor(undefined)}>
                  Cancel
                </Button>
                <Button type="submit" variant="primary" disabled={!nextPassword || reset.isPending}>
                  Save password
                </Button>
              </>
            }
          >
            {reset.isError && <Alert>{problemMessage(reset.error)}</Alert>}
            <Field
              id="reset-password"
              label="New password"
              type="password"
              value={nextPassword}
              onChange={setNextPassword}
              required
            />
          </SettingsSection>
        </form>
      )}
      <ConfirmDialog
        open={!!remove}
        onOpenChange={(open) => !open && setRemove(undefined)}
        title="Delete this user?"
        description={`${remove?.name} is signed out, anonymized and removed from groups and workspaces. The user name can be used again.`}
        confirm="Delete user"
        busy={removeUser.isPending}
        error={removeUser.error}
        onConfirm={() => remove && removeUser.mutate(remove.id)}
      />
    </>
  );
}

function Groups() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(groupsQuery);
  const { data: users } = useQuery(usersQuery);
  const [name, setName] = useState('');
  const [open, setOpen] = useState<string>();
  const members = useQuery({
    queryKey: ['groups', open, 'members'],
    enabled: !!open,
    queryFn: async () => (await api.v10.groups.byId(open!).members.get()) ?? [],
  });
  const create = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.groups.post({ name: name.trim() }),
    onSuccess: async () => {
      setName('');
      toast.success('Group created.');
      await queryClient.invalidateQueries({ queryKey: groupsQuery.queryKey });
    },
  });
  const add = useMutation({
    mutationFn: (userId: string) => api.v10.groups.byId(open!).members.post({ userId }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['groups', open, 'members'] }),
  });
  const remove = useMutation({
    mutationFn: (userId: string) => api.v10.groups.byId(open!).members.byUserId(userId).delete(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['groups', open, 'members'] }),
  });
  const [rename, setRename] = useState('');
  const saveName = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.groups.byId(open!).patch({ name: rename.trim() }),
    onSuccess: async () => {
      toast.success('Group renamed.');
      await queryClient.invalidateQueries({ queryKey: groupsQuery.queryKey });
    },
  });
  const [removeGroup, setRemoveGroup] = useState<{ id: string; name: string }>();
  const deleteGroup = useMutation({
    meta: { silent: true },
    mutationFn: (id: string) => api.v10.groups.byId(id).delete(),
    onSuccess: async () => {
      setOpen(undefined);
      setRemoveGroup(undefined);
      toast.success('Group deleted.');
      await queryClient.invalidateQueries({ queryKey: groupsQuery.queryKey });
    },
  });

  return (
    <>
      <SettingsSection title="Groups" description="A group can share an inbox and be granted access together.">
        {isPending ? (
          <Skeleton className="h-16" />
        ) : data?.length ? (
          <ul className="divide-y">
            {data.map((group) => (
              <li key={group.id} className="py-2 text-[13px]">
                <button
                  type="button"
                  className="font-medium hover:underline"
                  onClick={() => {
                    setOpen(group.id!);
                    setRename(group.name ?? '');
                  }}
                >
                  {group.name}
                </button>
                {open === group.id && (
                  <div className="mt-2 flex flex-col gap-2">
                    <div className="flex flex-wrap items-end gap-2">
                      <Field id="group-rename" label="Name" value={rename} onChange={setRename} />
                      <Button
                        size="sm"
                        disabled={!rename.trim() || rename.trim() === group.name || saveName.isPending}
                        onClick={() => saveName.mutate()}
                      >
                        Rename
                      </Button>
                      <Button
                        size="sm"
                        onClick={() => setRemoveGroup({ id: group.id!, name: group.name ?? 'this group' })}
                      >
                        Delete group
                      </Button>
                    </div>
                    {saveName.isError && <Alert>{problemMessage(saveName.error)}</Alert>}
                    <ul>
                      {(members.data ?? []).map((member) => (
                        <li key={member.id} className="flex items-center gap-2 py-1">
                          <span className="flex-1">{member.displayName || member.userName}</span>
                          <Button size="sm" onClick={() => remove.mutate(member.id!)}>
                            Remove
                          </Button>
                        </li>
                      ))}
                    </ul>
                    <Select
                      aria-label="Add a member"
                      value=""
                      onChange={(e) => e.target.value && add.mutate(e.target.value)}
                    >
                      <option value="">Add a person…</option>
                      {(users ?? []).map((user) => (
                        <option key={user.id} value={user.id!}>
                          {user.displayName || user.userName}
                        </option>
                      ))}
                    </Select>
                  </div>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState title="No groups yet" />
        )}
        {create.isError && <Alert className="mt-3">{problemMessage(create.error)}</Alert>}
      </SettingsSection>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          create.mutate();
        }}
      >
        <SettingsSection
          title="New group"
          actions={
            <Button type="submit" variant="primary" disabled={!name.trim() || create.isPending}>
              Create group
            </Button>
          }
        >
          <Field id="new-group-name" label="Name" value={name} onChange={setName} required />
        </SettingsSection>
      </form>
      <ConfirmDialog
        open={!!removeGroup}
        onOpenChange={(open) => !open && setRemoveGroup(undefined)}
        title="Delete this group?"
        description={`${removeGroup?.name} is removed. Grants and memberships that named it are cleaned up.`}
        confirm="Delete group"
        busy={deleteGroup.isPending}
        error={deleteGroup.error}
        onConfirm={() => removeGroup && deleteGroup.mutate(removeGroup.id)}
      />
    </>
  );
}

function Roles() {
  const queryClient = useQueryClient();
  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: async () => (await api.v10.roles.get()) ?? [],
  });
  const scopes = useQuery({
    queryKey: ['scopes'],
    queryFn: async () => (await api.v10.scopes.get()) ?? [],
  });
  const { data: users } = useQuery(usersQuery);
  const { data: groups } = useQuery(groupsQuery);
  const [name, setName] = useState('');
  const [chosen, setChosen] = useState<string[]>([]);
  const [roleId, setRoleId] = useState('');
  const [principalType, setPrincipalType] = useState<'user' | 'group'>('user');
  const [principalId, setPrincipalId] = useState('');
  const create = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.roles.post({ name: name.trim(), scopes: chosen }),
    onSuccess: async () => {
      setName('');
      setChosen([]);
      toast.success('Role created.');
      await queryClient.invalidateQueries({ queryKey: ['roles'] });
    },
  });
  const assign = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.roles.byId(roleId).assignments.post({ principalId, principalType }),
    onSuccess: async () => {
      setPrincipalId('');
      toast.success('Role assigned.');
      await queryClient.invalidateQueries({ queryKey: ['roles', roleId, 'assignments'] });
    },
  });
  const assignments = useQuery({
    queryKey: ['roles', roleId, 'assignments'],
    enabled: !!roleId,
    queryFn: async () => (await api.v10.roles.byId(roleId).assignments.get()) ?? [],
  });
  const unassign = useMutation({
    mutationFn: (assignmentId: string) => api.v10.roles.byId(roleId).assignments.byAssignmentId(assignmentId).delete(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['roles', roleId, 'assignments'] }),
  });
  const [removeRole, setRemoveRole] = useState<{ id: string; name: string }>();
  const deleteRole = useMutation({
    meta: { silent: true },
    mutationFn: (id: string) => api.v10.roles.byId(id).delete(),
    onSuccess: async () => {
      if (removeRole?.id === roleId) setRoleId('');
      setRemoveRole(undefined);
      toast.success('Role deleted.');
      await queryClient.invalidateQueries({ queryKey: ['roles'] });
    },
  });

  return (
    <>
      <SettingsSection title="Roles" description="A role is a set of scopes. Assign it to a person.">
        {roles.isPending ? (
          <Skeleton className="h-16" />
        ) : (
          <ul className="divide-y">
            {(roles.data ?? []).map((role) => (
              <li key={role.id} className="py-2 text-[13px]">
                <span className="font-medium">{role.name}</span>
                {role.isBuiltIn && <span className="ml-2 text-muted">Built-in</span>}
                {!role.isBuiltIn && (
                  <Button
                    size="sm"
                    className="ml-2"
                    onClick={() => setRemoveRole({ id: role.id!, name: role.name ?? 'this role' })}
                  >
                    Delete
                  </Button>
                )}
                <p className="text-xs text-muted">{role.grantsAllScopes ? 'Every scope' : role.scopes?.join(', ')}</p>
              </li>
            ))}
          </ul>
        )}
      </SettingsSection>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          create.mutate();
        }}
      >
        <SettingsSection
          title="New role"
          actions={
            <Button type="submit" variant="primary" disabled={!name.trim() || !chosen.length || create.isPending}>
              Create role
            </Button>
          }
        >
          {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
          <Field id="new-role-name" label="Name" value={name} onChange={setName} required />
          <ul className="mt-3 grid gap-1 sm:grid-cols-2">
            {(scopes.data ?? []).map((scope) => (
              <li key={scope.name}>
                <label className="flex items-start gap-2 text-[13px]">
                  <Checkbox
                    checked={chosen.includes(scope.name!)}
                    onChange={(e) =>
                      setChosen(e.target.checked ? [...chosen, scope.name!] : chosen.filter((s) => s !== scope.name))
                    }
                  />
                  <span>
                    <code className="text-xs">{scope.name}</code>
                    {scope.description && <span className="block text-xs text-muted">{scope.description}</span>}
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </SettingsSection>
      </form>
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          assign.mutate();
        }}
      >
        <SettingsSection
          title="Assign a role"
          actions={
            <Button type="submit" variant="primary" disabled={!roleId || !principalId || assign.isPending}>
              Assign
            </Button>
          }
        >
          {assign.isError && <Alert>{problemMessage(assign.error)}</Alert>}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="assign-role">Role</Label>
              <Select id="assign-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
                <option value="">Choose a role…</option>
                {(roles.data ?? []).map((role) => (
                  <option key={role.id} value={role.id!}>
                    {role.name}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="assign-kind">Assign to</Label>
              <Select
                id="assign-kind"
                value={principalType}
                onChange={(e) => {
                  setPrincipalType(e.target.value === 'group' ? 'group' : 'user');
                  setPrincipalId('');
                }}
              >
                <option value="user">A person</option>
                <option value="group">A group</option>
              </Select>
            </div>
            <div className="flex flex-col gap-1.5 sm:col-span-2">
              <Label htmlFor="assign-principal">{principalType === 'group' ? 'Group' : 'Person'}</Label>
              <Select id="assign-principal" value={principalId} onChange={(e) => setPrincipalId(e.target.value)}>
                <option value="">{principalType === 'group' ? 'Choose a group…' : 'Choose a person…'}</option>
                {principalType === 'group'
                  ? (groups ?? []).map((group) => (
                      <option key={group.id} value={group.id!}>
                        {group.name}
                      </option>
                    ))
                  : (users ?? []).map((user) => (
                      <option key={user.id} value={user.id!}>
                        {user.displayName || user.userName}
                      </option>
                    ))}
              </Select>
            </div>
          </div>
          {!!roleId && (
            <ul className="mt-3 divide-y text-[13px]">
              {(assignments.data ?? []).map((assignment) => {
                const person = users?.find((user) => user.id === assignment.principalId);
                const group = groups?.find((item) => item.id === assignment.principalId);
                const label =
                  assignment.principalType === 'group'
                    ? (group?.name ?? 'A group')
                    : person?.displayName || person?.userName || 'A person';
                return (
                  <li key={assignment.id} className="flex items-center gap-2 py-1.5">
                    <span className="flex-1">
                      {label}
                      <span className="ml-2 text-muted">
                        {assignment.principalType === 'group' ? 'Group' : 'Person'}
                      </span>
                    </span>
                    <Button size="sm" disabled={unassign.isPending} onClick={() => unassign.mutate(assignment.id!)}>
                      Remove
                    </Button>
                  </li>
                );
              })}
              {!assignments.isPending && !assignments.data?.length && (
                <li className="py-1.5 text-muted">Nobody has this role yet.</li>
              )}
            </ul>
          )}
        </SettingsSection>
      </form>
      <ConfirmDialog
        open={!!removeRole}
        onOpenChange={(open) => !open && setRemoveRole(undefined)}
        title="Delete this role?"
        description={`${removeRole?.name} and its assignments are removed. Built-in roles stay.`}
        confirm="Delete role"
        busy={deleteRole.isPending}
        error={deleteRole.error}
        onConfirm={() => removeRole && deleteRole.mutate(removeRole.id)}
      />
    </>
  );
}

function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
  required,
}: {
  id?: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  type?: string;
  required?: boolean;
}) {
  const fieldId = id ?? label.toLowerCase().replace(/\W+/g, '-');
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={fieldId}>{label}</Label>
      <Input id={fieldId} type={type} required={required} value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}
