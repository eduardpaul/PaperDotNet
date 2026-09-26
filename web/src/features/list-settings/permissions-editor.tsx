import type { PermissionGrantDto, PermissionsResponse, PrincipalType, WorkspaceAccessLevel } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link2, Link2Off, Trash2, UserPlus, Users } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { Avatar } from '@/components/ui/avatar';
import { Button } from '@/components/ui/button';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Select } from '@/components/ui/select';
import { groupsQuery, userName, usersQuery } from '@/features/fields/directory';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

/** The permission endpoints of a list or an item (the same shape for both). */
interface PermissionsBuilder {
  get(): Promise<PermissionsResponse | undefined>;
  breakInheritance: { post(body: { copyGrants?: boolean | null }): Promise<PermissionsResponse | undefined> };
  resetInheritance: { post(): Promise<void> };
  grants: { put(body: { grants: PermissionGrantDto[] }): Promise<PermissionsResponse | undefined> };
}

const levels: { value: WorkspaceAccessLevel; label: string }[] = [
  { value: 'read', label: 'Can read' },
  { value: 'contribute', label: 'Can edit' },
  { value: 'manage', label: 'Can manage' },
];

type Grant = { principalType: PrincipalType; principalId: string; level: WorkspaceAccessLevel };
const grantsOf = (response: PermissionsResponse | undefined): Grant[] =>
  (response?.grants ?? []).map((g) => ({
    principalType: g.principalType ?? 'user',
    principalId: g.principalId!,
    level: g.level ?? 'read',
  }));

/**
 * Who may read, edit or manage a list or an item (IAM-07). Permissions are inherited (from the workspace, or the
 * list for items) until they are made unique; then grants for people and groups replace them.
 */
export function PermissionsEditor({
  queryKey,
  builder,
  scope,
}: {
  queryKey: readonly unknown[];
  builder: PermissionsBuilder;
  /** "list", "library" or "item", for the texts. */
  scope: string;
}) {
  const queryClient = useQueryClient();
  const { data: permissions, isPending } = useQuery({
    queryKey,
    queryFn: async () => (await builder.get())!,
  });
  const { data: users } = useQuery(usersQuery);
  const { data: groups } = useQuery(groupsQuery);
  const [grants, setGrants] = useState<Grant[]>([]);
  const [baseline, setBaseline] = useState<PermissionsResponse>();
  const [adding, setAdding] = useState<ComboboxOption[]>([]);
  const [addLevel, setAddLevel] = useState<WorkspaceAccessLevel>('contribute');
  const [resetting, setResetting] = useState(false);
  if (permissions && permissions !== baseline) {
    setBaseline(permissions);
    setGrants(grantsOf(permissions));
  }
  const done = (updated: PermissionsResponse | undefined, message: string) => {
    toast.success(message);
    if (updated) queryClient.setQueryData(queryKey, updated);
    else return queryClient.invalidateQueries({ queryKey });
  };
  const breakInheritance = useMutation({
    mutationFn: () => builder.breakInheritance.post({ copyGrants: true }),
    onSuccess: (updated) => done(updated, 'Permissions are now unique. Change them below.'),
  });
  const reset = useMutation({
    meta: { silent: true },
    mutationFn: () => builder.resetInheritance.post(),
    onSuccess: async () => {
      setResetting(false);
      await done(undefined, 'Permissions are inherited again.');
    },
  });
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => builder.grants.put({ grants }),
    onSuccess: (updated) => done(updated, 'Permissions saved.'),
  });

  if (isPending || !permissions) return <Skeleton className="h-64" />;
  const canManage = permissions.effectiveLevel === 'manage';
  const unique = !!permissions.hasUniquePermissions;
  const nameOf = (grant: Grant) =>
    grant.principalType === 'group'
      ? (groups?.find((g) => g.id === grant.principalId)?.name ?? 'Unknown group')
      : userName(
          users?.find((u) => u.id === grant.principalId),
          grant.principalId,
        );
  const taken = new Set(grants.map((g) => `${g.principalType}:${g.principalId}`));
  const options: ComboboxOption[] = [
    ...(groups ?? []).map((g) => ({
      value: `group:${g.id}`,
      label: g.name ?? '',
      hint: 'group',
      icon: <Users className="size-3.5" />,
    })),
    ...(users ?? [])
      .filter((u) => !u.isDisabled)
      .map((u) => ({ value: `user:${u.id}`, label: userName(u, u.id!), hint: u.email ?? u.userName ?? undefined })),
  ].filter((o) => !taken.has(o.value));
  const dirty = JSON.stringify(grants) !== JSON.stringify(grantsOf(permissions));
  const add = () => {
    setGrants([
      ...grants,
      ...adding.map((o) => {
        const [principalType, principalId] = o.value.split(':') as [PrincipalType, string];
        return { principalType, principalId, level: addLevel };
      }),
    ]);
    setAdding([]);
  };

  return (
    <>
      <SettingsSection
        title="Permissions"
        description={
          unique
            ? `This ${scope} has its own permissions.`
            : `This ${scope} inherits its permissions from ${permissions.inheritsFrom ?? 'its workspace'}. Workspace owners can manage it, members edit and visitors read.`
        }
        actions={
          canManage &&
          (unique ? (
            <>
              <Button className="mr-auto" onClick={() => setResetting(true)}>
                <Link2 /> Inherit again
              </Button>
              {dirty && <span className="text-xs text-muted">Unsaved changes</span>}
              <Button variant="primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>
                Save
              </Button>
            </>
          ) : (
            <Button disabled={breakInheritance.isPending} onClick={() => breakInheritance.mutate()}>
              <Link2Off /> Give it its own permissions
            </Button>
          ))
        }
        className="px-0 pb-0"
      >
        {!canManage ? (
          <p className="border-t px-5 py-4 text-[13px] text-muted">
            You can{' '}
            {levels
              .find((l) => l.value === permissions.effectiveLevel)
              ?.label.toLowerCase()
              .replace('can ', '')}{' '}
            this {scope}. Only people who manage it see who else has access.
          </p>
        ) : (
          <ul className="divide-y border-t">
            {grants.map((grant, index) => {
              const name = nameOf(grant);
              return (
                <li key={`${grant.principalType}:${grant.principalId}`} className="flex items-center gap-3 px-5 py-2.5">
                  {grant.principalType === 'group' ? (
                    <span className="flex size-8 items-center justify-center rounded-full bg-surface-muted">
                      <Users className="size-4 text-muted" />
                    </span>
                  ) : (
                    <Avatar name={name} className="size-8" />
                  )}
                  <span className="min-w-0 flex-1 truncate text-[13px] font-medium">{name}</span>
                  <Select
                    aria-label={`Access of ${name}`}
                    className="w-36"
                    disabled={!unique}
                    value={grant.level}
                    onChange={(e) =>
                      setGrants(
                        grants.map((g, i) =>
                          i === index ? { ...g, level: e.target.value as WorkspaceAccessLevel } : g,
                        ),
                      )
                    }
                  >
                    {levels.map((l) => (
                      <option key={l.value} value={l.value}>
                        {l.label}
                      </option>
                    ))}
                  </Select>
                  {unique && (
                    <Button
                      size="icon"
                      variant="ghost"
                      aria-label={`Remove access of ${name}`}
                      onClick={() => setGrants(grants.filter((_, i) => i !== index))}
                    >
                      <Trash2 />
                    </Button>
                  )}
                </li>
              );
            })}
            {unique && (
              <li className="flex flex-wrap items-center gap-2 bg-surface-muted/30 px-5 py-3">
                <span id="grant-people-label" className="sr-only">
                  People or groups
                </span>
                <div className="min-w-56 flex-1">
                  <Combobox
                    aria-labelledby="grant-people-label"
                    multiple
                    selected={adding}
                    options={options}
                    placeholder="Add people or groups…"
                    onChange={setAdding}
                  />
                </div>
                <Select
                  aria-label="Access to give"
                  className="w-36"
                  value={addLevel}
                  onChange={(e) => setAddLevel(e.target.value as WorkspaceAccessLevel)}
                >
                  {levels.map((l) => (
                    <option key={l.value} value={l.value}>
                      {l.label}
                    </option>
                  ))}
                </Select>
                <Button disabled={!adding.length} onClick={add}>
                  <UserPlus /> Add
                </Button>
              </li>
            )}
          </ul>
        )}
        {(save.isError || breakInheritance.isError) && (
          <Alert className="m-5">{problemMessage(save.error ?? breakInheritance.error)}</Alert>
        )}
      </SettingsSection>
      <ConfirmDialog
        open={resetting}
        onOpenChange={setResetting}
        title="Inherit permissions again?"
        description={`The grants of this ${scope} are removed; it gets the permissions of ${permissions.inheritsFrom ?? 'its parent'} again.`}
        confirm="Inherit again"
        busy={reset.isPending}
        error={reset.error}
        onConfirm={() => reset.mutate()}
      />
    </>
  );
}
