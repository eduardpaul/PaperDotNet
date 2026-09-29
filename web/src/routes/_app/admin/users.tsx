import type { UserResponse } from '@paperdotnet/client';
import { all, toArray } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { KeyRound, Pencil, Plus, Search, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { meQuery } from '@/api/queries';
import { Avatar } from '@/components/ui/avatar';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Field } from '@/features/admin/common';
import { Checkbox } from '@/components/ui/select';
import { usersQuery } from '@/features/fields/directory';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/admin/users')({ component: Users });

/** Every account (IAM-14): create, edit, disable, reset passwords, delete. */
const allUsersQuery = {
  queryKey: ['admin', 'users'],
  queryFn: () => toArray(all(api.v10.users, { queryParameters: { top: 200 } })),
};

function Users() {
  const format = useFormat();
  const queryClient = useQueryClient();
  const { data: me } = useQuery(meQuery);
  const { data: users, isPending } = useQuery(allUsersQuery);
  const [filter, setFilter] = useState('');
  const [editing, setEditing] = useState<UserResponse | 'new'>();
  const [password, setPassword] = useState<UserResponse>();
  const [deleting, setDeleting] = useState<UserResponse>();
  const invalidate = async () => {
    await queryClient.invalidateQueries({ queryKey: allUsersQuery.queryKey });
    await queryClient.invalidateQueries({ queryKey: usersQuery.queryKey });
  };
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (user: UserResponse) => api.v10.users.byId(user.id!).delete(),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('User deleted.');
      await invalidate();
    },
  });
  const text = filter.trim().toLowerCase();
  const shown = (users ?? []).filter(
    (u) => !text || [u.userName, u.displayName, u.email].some((v) => v?.toLowerCase().includes(text)),
  );

  return (
    <>
      <SettingsSection
        title="Users"
        description={`${users?.length ?? 0} accounts. Roles decide what they may administer.`}
        className="px-0 pb-0"
        actions={
          <Button variant="primary" onClick={() => setEditing('new')}>
            <Plus /> New user
          </Button>
        }
      >
        <div className="relative mx-5 mb-3 max-w-xs">
          <Search className="absolute top-2.5 left-2.5 size-4 text-muted" />
          <Input
            aria-label="Find users"
            className="pl-8"
            placeholder="Find…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
        </div>
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-32" />
        ) : shown.length ? (
          <ul className="divide-y border-t">
            {shown.map((user) => (
              <li key={user.id} className="flex items-center gap-3 px-5 py-2.5">
                <Avatar name={user.displayName ?? user.userName} className="size-8" />
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 truncate text-[13px] font-medium">
                    {user.displayName ?? user.userName}
                    {user.isDisabled && <Badge tone="danger">Disabled</Badge>}
                    {user.id === me?.id && <span className="font-normal text-muted">(you)</span>}
                  </p>
                  <p className="truncate text-xs text-muted">
                    {user.userName}
                    {user.email && ` · ${user.email}`} · since {format.date(user.createdAt)}
                  </p>
                </div>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={`Edit ${user.userName}`}
                  onClick={() => setEditing(user)}
                >
                  <Pencil />
                </Button>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={`Set a password for ${user.userName}`}
                  onClick={() => setPassword(user)}
                >
                  <KeyRound />
                </Button>
                {user.id !== me?.id && (
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`Delete ${user.userName}`}
                    onClick={() => setDeleting(user)}
                  >
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState title="No users match" className="border-t py-8" />
        )}
      </SettingsSection>
      {editing && (
        <UserDialog
          user={editing === 'new' ? undefined : editing}
          onClose={() => setEditing(undefined)}
          onSaved={invalidate}
        />
      )}
      {password && <PasswordDialog user={password} onClose={() => setPassword(undefined)} />}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(undefined)}
        title={`Delete ${deleting?.userName ?? ''}?`}
        description="The account, its sessions and tokens go away; what the user created stays. This cannot be undone."
        confirm="Delete user"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  );
}

function UserDialog({
  user,
  onClose,
  onSaved,
}: {
  user?: UserResponse;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const [userName, setUserName] = useState(user?.userName ?? '');
  const [displayName, setDisplayName] = useState(user?.displayName ?? '');
  const [email, setEmail] = useState(user?.email ?? '');
  const [password, setPassword] = useState('');
  const [disabled, setDisabled] = useState(!!user?.isDisabled);
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      user
        ? api.v10.users
            .byId(user.id!)
            .patch({ displayName: displayName.trim(), email: email.trim(), isDisabled: disabled })
        : api.v10.users.post({
            userName: userName.trim(),
            password,
            displayName: displayName.trim() || undefined,
            email: email.trim() || undefined,
          }),
    onSuccess: async () => {
      toast.success(user ? 'User saved.' : 'User created.');
      await onSaved();
      onClose();
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{user ? `Edit ${user.userName}` : 'New user'}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <Field id="user-name" label="User name">
              <Input
                id="user-name"
                required
                disabled={!!user}
                autoComplete="off"
                value={userName}
                onChange={(e) => setUserName(e.target.value)}
              />
            </Field>
            <Field id="user-display" label="Display name">
              <Input id="user-display" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
            </Field>
            <Field id="user-email" label="Email">
              <Input id="user-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
            </Field>
            {user ? (
              <label className="flex items-center gap-2 text-[13px]">
                <Checkbox checked={disabled} onChange={(e) => setDisabled(e.target.checked)} />
                Disabled: cannot sign in, sessions and tokens end
              </label>
            ) : (
              <Field id="user-password" label="Password" hint="At least 10 characters. The user can change it.">
                <Input
                  id="user-password"
                  type="password"
                  required
                  minLength={10}
                  autoComplete="new-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </Field>
            )}
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={save.isPending}>
              {user ? 'Save' : 'Create user'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function PasswordDialog({ user, onClose }: { user: UserResponse; onClose: () => void }) {
  const [password, setPassword] = useState('');
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.users.byId(user.id!).password.post({ password }),
    onSuccess: () => {
      toast.success(`New password set for ${user.userName}. Their sessions ended.`);
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <DialogHeader>
            <DialogTitle>Set a password for {user.userName}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <Field
              id="reset-password"
              label="New password"
              hint="Tell the user; they should change it after signing in."
            >
              <Input
                id="reset-password"
                type="password"
                required
                minLength={10}
                autoComplete="new-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </Field>
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={save.isPending}>
              Set password
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
