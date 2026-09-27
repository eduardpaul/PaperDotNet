import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { CopyField, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/admin/applications')({ component: Applications });

const applicationsQuery = {
  queryKey: ['applications'],
  queryFn: async () => (await api.v10.applications.get()) ?? [],
};

function Applications() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(applicationsQuery);
  const [name, setName] = useState('');
  const [clientType, setClientType] = useState('public');
  const [redirect, setRedirect] = useState('');
  const [secret, setSecret] = useState<string>();
  const create = useMutation({
    meta: { silent: true },
    mutationFn: () =>
      api.v10.applications.post({
        displayName: name.trim(),
        clientType,
        grantTypes: clientType === 'confidential' ? ['client_credentials'] : ['authorization_code', 'refresh_token'],
        redirectUris: redirect.trim() ? [redirect.trim()] : undefined,
        scopes: ['api'],
      }),
    onSuccess: async (created) => {
      setName('');
      setRedirect('');
      setSecret(created?.clientSecret ?? undefined);
      toast.success('Application created.');
      await queryClient.invalidateQueries({ queryKey: applicationsQuery.queryKey });
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.v10.applications.byId(id).delete(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: applicationsQuery.queryKey }),
  });

  return (
    <>
      <SettingsSection title="Applications" description="OAuth clients for scripts and other apps in this organization.">
        {isPending ? (
          <Skeleton className="h-16" />
        ) : (
          <ul className="divide-y text-[13px]">
            {(data ?? []).map((app) => (
              <li key={app.id} className="flex items-center gap-2 py-2">
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{app.displayName}</span>
                  <span className="ml-2 text-muted">{app.clientId}</span>
                </span>
                {!app.isFirstParty && (
                  <Button size="sm" onClick={() => remove.mutate(app.id!)}>
                    Delete
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </SettingsSection>
      {secret && (
        <SettingsSection title="Client secret" description="Shown once. Copy it now.">
          <CopyField value={secret} label="Client secret" />
        </SettingsSection>
      )}
      <form
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          create.mutate();
        }}
      >
        <SettingsSection
          title="New application"
          actions={
            <Button type="submit" variant="primary" disabled={!name.trim() || create.isPending}>
              Create application
            </Button>
          }
        >
          {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="app-name">Name</Label>
              <Input id="app-name" required value={name} onChange={(e) => setName(e.target.value)} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="app-type">Type</Label>
              <Select id="app-type" value={clientType} onChange={(e) => setClientType(e.target.value)}>
                <option value="public">Public (a browser or device, no secret)</option>
                <option value="confidential">Confidential (a server, with a secret)</option>
              </Select>
            </div>
            <div className="flex flex-col gap-1.5 sm:col-span-2">
              <Label htmlFor="app-redirect">Redirect URL</Label>
              <Input
                id="app-redirect"
                placeholder="https://…"
                value={redirect}
                onChange={(e) => setRedirect(e.target.value)}
              />
            </div>
          </div>
        </SettingsSection>
      </form>
    </>
  );
}
