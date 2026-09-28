import type { RoleResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { ChevronRight, Pencil, Plus, ShieldHalf, Trash2, UserPlus, Users, X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input, Textarea } from '@/components/ui/input';
import { Checkbox } from '@/components/ui/select';
import { Field } from '@/features/admin/common';
import { groupsQuery, userName, usersQuery } from '@/features/fields/directory';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

export const Route = createFileRoute('/_app/admin/roles')({ component: Roles });

const rolesQuery = { queryKey: ['roles'], queryFn: async () => (await api.v10.roles.get()) ?? [] };
const scopesQuery = {
  queryKey: ['scopes'],
  queryFn: async () => (await api.v10.scopes.get()) ?? [],
  staleTime: Infinity,
};

/** Roles bundle scopes (IAM-05) and are given to people and groups; Administrator holds everything. */
function Roles() {
  const queryClient = useQueryClient();
  const { data: roles, isPending } = useQuery(rolesQuery);
  const [editing, setEditing] = useState<RoleResponse | 'new'>();
  const [deleting, setDeleting] = useState<RoleResponse>();
  const [open, setOpen] = useState<string>();
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (role: RoleResponse) => api.v10.roles.byId(role.id!).delete(),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('Role deleted.');
      await queryClient.invalidateQueries({ queryKey: rolesQuery.queryKey });
    },
  });
  return (
    <>
      <SettingsSection
        title="Roles"
        description="What people may do across the organization. Workspace and list access is set on the workspace and list."
        className="px-0 pb-0"
        actions={
          <Button variant="primary" onClick={() => setEditing('new')}>
            <Plus /> New role
          </Button>
        }
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-24" />
        ) : (
          <ul className="divide-y border-t">
            {(roles ?? []).map((role) => (
              <li key={role.id}>
                <div className="flex items-center gap-3 px-5 py-2.5">
                  <button
                    type="button"
                    aria-expanded={open === role.id}
                    className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    onClick={() => setOpen(open === role.id ? undefined : role.id!)}
                  >
                    <ChevronRight
                      className={cn('size-4 text-muted transition-transform', open === role.id && 'rotate-90')}
                    />
                    <ShieldHalf className="size-4 text-muted" />
                    <span className="min-w-0">
                      <span className="flex items-center gap-2 text-[13px] font-medium">
                        {role.name}
                        {role.isBuiltIn && <Badge>Built in</Badge>}
                      </span>
                      <span className="block truncate text-xs text-muted">
                        {role.grantsAllScopes ? 'Everything' : `${role.scopes?.length ?? 0} scopes`}
                        {role.description && ` · ${role.description}`}
                      </span>
                    </span>
                  </button>
                  <Button size="icon" variant="ghost" aria-label={`Edit ${role.name}`} onClick={() => setEditing(role)}>
                    <Pencil />
                  </Button>
                  {!role.isBuiltIn && (
                    <Button
                      size="icon"
                      variant="ghost"
                      aria-label={`Delete ${role.name}`}
                      onClick={() => setDeleting(role)}
                    >
                      <Trash2 />
                    </Button>
                  )}
                </div>
                {open === role.id && <Assignments role={role} />}
              </li>
            ))}
          </ul>
        )}
      </SettingsSection>
      {editing && <RoleDialog role={editing === 'new' ? undefined : editing} onClose={() => setEditing(undefined)} />}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(o) => !o && setDeleting(undefined)}
        title={`Delete the role “${deleting?.name ?? ''}”?`}
        description="Everyone who has it loses its scopes."
        confirm="Delete role"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  );
}

function Assignments({ role }: { role: RoleResponse }) {
  const queryClient = useQueryClient();
  const key = ['roles', role.id, 'assignments'];
  const { data: assignments } = useQuery({
    queryKey: key,
    queryFn: async () => (await api.v10.roles.byId(role.id!).assignments.get()) ?? [],
  });
  const { data: users } = useQuery(usersQuery);
  const { data: groups } = useQuery(groupsQuery);
  const [adding, setAdding] = useState<ComboboxOption[]>([]);
  const refresh = () => queryClient.invalidateQueries({ queryKey: key });
  const add = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      for (const option of adding) {
        const [principalType, principalId] = option.value.split(':') as ['user' | 'group', string];
        await api.v10.roles.byId(role.id!).assignments.post({ principalType, principalId });
      }
    },
    onSuccess: async () => {
      setAdding([]);
      await refresh();
    },
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (id: string) => api.v10.roles.byId(role.id!).assignments.byAssignmentId(id).delete(),
    onSuccess: refresh,
  });
  const nameOf = (type: string | null | undefined, id: string) =>
    type === 'group'
      ? (groups?.find((g) => g.id === id)?.name ?? 'Unknown group')
      : userName(
          users?.find((u) => u.id === id),
          id,
        );
  const taken = new Set((assignments ?? []).map((a) => `${a.principalType}:${a.principalId}`));
  const options: ComboboxOption[] = [
    ...(groups ?? []).map((g) => ({
      value: `group:${g.id}`,
      label: g.name ?? '',
      hint: 'group',
      icon: <Users className="size-3.5" />,
    })),
    ...(users ?? [])
      .filter((u) => !u.isDisabled)
      .map((u) => ({ value: `user:${u.id}`, label: userName(u, u.id!), hint: u.userName ?? undefined })),
  ].filter((o) => !taken.has(o.value));
  return (
    <div className="flex flex-col gap-2 border-t bg-surface-muted/30 px-5 py-3 pl-12">
      <div className="flex flex-wrap gap-1.5">
        {(assignments ?? []).map((a) => {
          const name = nameOf(a.principalType, a.principalId!);
          return (
            <span
              key={a.id}
              className="inline-flex items-center gap-1 rounded bg-surface px-2 py-0.5 text-xs shadow-xs"
            >
              {a.principalType === 'group' && <Users className="size-3 text-muted" />}
              {name}
              <button
                type="button"
                aria-label={`Take ${role.name} from ${name}`}
                className="text-muted hover:text-foreground"
                onClick={() => remove.mutate(a.id!)}
              >
                <X className="size-3" />
              </button>
            </span>
          );
        })}
        {assignments?.length === 0 && <span className="text-xs text-muted">Nobody has this role.</span>}
      </div>
      <div className="flex flex-wrap gap-2">
        <span id={`assign-${role.id}`} className="sr-only">
          Give {role.name} to
        </span>
        <div className="min-w-56 flex-1">
          <Combobox
            aria-labelledby={`assign-${role.id}`}
            multiple
            selected={adding}
            options={options}
            placeholder="Give to people or groups…"
            onChange={setAdding}
          />
        </div>
        <Button disabled={!adding.length || add.isPending} onClick={() => add.mutate()}>
          <UserPlus /> Give role
        </Button>
      </div>
      {(add.isError || remove.isError) && <Alert>{problemMessage(add.error ?? remove.error)}</Alert>}
    </div>
  );
}

function RoleDialog({ role, onClose }: { role?: RoleResponse; onClose: () => void }) {
  const queryClient = useQueryClient();
  const { data: catalog } = useQuery(scopesQuery);
  const [name, setName] = useState(role?.name ?? '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [scopes, setScopes] = useState<string[]>(role?.scopes ?? []);
  const readOnly = !!role?.grantsAllScopes;
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      role
        ? api.v10.roles.byId(role.id!).patch({ name: name.trim(), description: description.trim(), scopes })
        : api.v10.roles.post({ name: name.trim(), description: description.trim() || undefined, scopes }),
    onSuccess: async () => {
      toast.success(role ? 'Role saved.' : 'Role created.');
      await queryClient.invalidateQueries({ queryKey: rolesQuery.queryKey });
      onClose();
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-xl">
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{role ? `Role “${role.name}”` : 'New role'}</DialogTitle>
          </DialogHeader>
          <fieldset disabled={readOnly} className="flex max-h-[62vh] flex-col gap-3 overflow-y-auto px-5 pb-4">
            <Field id="role-name" label="Name">
              <Input
                id="role-name"
                required
                disabled={!!role?.isBuiltIn}
                maxLength={200}
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
            </Field>
            <Field id="role-description" label="Description">
              <Textarea
                id="role-description"
                rows={2}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </Field>
            {readOnly ? (
              <Alert tone="warning">This role holds every scope, including those of extensions added later.</Alert>
            ) : (
              <fieldset>
                <legend className="mb-1.5 text-[13px] font-medium">Scopes</legend>
                <ul className="flex flex-col rounded-md border p-1">
                  {(catalog ?? []).map((scope) => (
                    <li key={scope.name}>
                      <label className="flex items-start gap-2 rounded px-1.5 py-1 text-[13px] hover:bg-surface-muted">
                        <Checkbox
                          className="mt-0.5"
                          checked={scopes.includes(scope.name!)}
                          onChange={(e) =>
                            setScopes(
                              e.target.checked ? [...scopes, scope.name!] : scopes.filter((s) => s !== scope.name),
                            )
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
              </fieldset>
            )}
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </fieldset>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              {readOnly ? 'Close' : 'Cancel'}
            </Button>
            {!readOnly && (
              <Button type="submit" variant="primary" disabled={!name.trim() || save.isPending}>
                {role ? 'Save' : 'Create role'}
              </Button>
            )}
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
