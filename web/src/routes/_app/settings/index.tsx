import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { meQuery } from '@/api/queries';
import { Avatar } from '@/components/ui/avatar';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/settings/')({ component: Profile });

/** How the user is shown to others; the user name and email are managed by administrators (IAM-14). */
function Profile() {
  const queryClient = useQueryClient();
  const { data: me } = useQuery(meQuery);
  const [name, setName] = useState(me?.displayName ?? '');
  const [baseline, setBaseline] = useState(me?.displayName);
  // Follow changes made elsewhere (another tab, an administrator) while nothing is being edited.
  if (me && me.displayName !== baseline) {
    if (name === (baseline ?? '')) setName(me.displayName ?? '');
    setBaseline(me.displayName);
  }
  const save = useMutation({
    mutationFn: async () => (await api.v10.me.patch({ displayName: name.trim() }))!,
    onSuccess: (updated) => {
      queryClient.setQueryData(keys.me, updated);
      setName(updated.displayName ?? '');
      toast.success('Profile saved.');
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  if (!me) return <Skeleton className="h-64" />;
  const changed = name.trim() !== (me.displayName ?? '');
  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Profile"
        description="How other people see you in comments, assignments and activity."
        actions={
          <Button type="submit" variant="primary" disabled={!changed || save.isPending}>
            Save
          </Button>
        }
      >
        <div className="mb-3 flex items-center gap-3">
          <Avatar name={name || me.userName} className="size-12 text-base" />
          <div className="min-w-0">
            <p className="truncate font-medium">{name || me.userName}</p>
            <p className="truncate text-xs text-muted">{me.email ?? me.userName}</p>
          </div>
        </div>
        <div className="divide-y">
          <SettingRow id="display-name" label="Display name" hint="Leave empty to use your user name.">
            <Input
              id="display-name"
              value={name}
              maxLength={200}
              autoComplete="name"
              onChange={(e) => setName(e.target.value)}
            />
          </SettingRow>
          <SettingRow id="user-name" label="User name" hint="You sign in with it. An administrator can change it.">
            <Input id="user-name" value={me.userName ?? ''} readOnly disabled />
          </SettingRow>
          <SettingRow id="email" label="Email" hint="Managed by an administrator.">
            <Input id="email" value={me.email ?? ''} placeholder="Not set" readOnly disabled />
          </SettingRow>
          <SettingRow id="organization" label="Organization">
            <Input id="organization" value={me.tenantIdentifier ?? ''} readOnly disabled />
          </SettingRow>
        </div>
        {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}
