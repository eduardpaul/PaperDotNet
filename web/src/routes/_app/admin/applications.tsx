import type { ApplicationResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { AppWindow, KeyRound, Plus, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { Field } from '@/features/admin/common';
import { ConfirmDialog, CopyField, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/admin/applications')({ component: Applications });

const applicationsQuery = { queryKey: ['applications'], queryFn: async () => (await api.v10.applications.get()) ?? [] };

const grantLabels: Record<string, string> = {
  authorization_code: 'Sign in users (authorization code)',
  client_credentials: 'Run as a service (client credentials)',
  refresh_token: 'Stay signed in (refresh tokens)',
};

/** OAuth applications (IAM-02): apps that sign users in, and services that call the API on their own. */
function Applications() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(applicationsQuery);
  const [creating, setCreating] = useState(false);
  const [secret, setSecret] = useState<{ name: string; clientId: string; secret: string }>();
  const [deleting, setDeleting] = useState<ApplicationResponse>();
  const rotate = useMutation({
    mutationFn: async (app: ApplicationResponse) => (await api.v10.applications.byId(app.id!).secret.post())!,
    onSuccess: (result) =>
      setSecret({
        name: result.application?.displayName ?? '',
        clientId: result.application?.clientId ?? '',
        secret: result.clientSecret ?? '',
      }),
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (app: ApplicationResponse) => api.v10.applications.byId(app.id!).delete(),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('Application deleted.');
      await queryClient.invalidateQueries({ queryKey: applicationsQuery.queryKey });
    },
  });
  return (
    <>
      <SettingsSection
        title="Applications"
        description="Programs that use PaperDotNet through OAuth: web apps signing people in, or services with their own account."
        className="px-0 pb-0"
        actions={
          <Button variant="primary" onClick={() => setCreating(true)}>
            <Plus /> New application
          </Button>
        }
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-24" />
        ) : data?.length ? (
          <ul className="divide-y border-t">
            {data.map((app) => (
              <li key={app.id} className="flex items-center gap-3 px-5 py-3">
                <AppWindow className="size-4 text-muted" />
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 text-[13px] font-medium">
                    {app.displayName ?? app.clientId}
                    <Badge>{app.clientType}</Badge>
                    {app.isFirstParty && <Badge tone="success">This app</Badge>}
                  </p>
                  <p className="truncate text-xs text-muted">
                    <code>{app.clientId}</code> · {(app.grantTypes ?? []).join(', ')}
                    {!!app.redirectUris?.length && ` · ${app.redirectUris.join(', ')}`}
                  </p>
                </div>
                {!app.isFirstParty && app.clientType === 'confidential' && (
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`New secret for ${app.displayName}`}
                    onClick={() => rotate.mutate(app)}
                  >
                    <KeyRound />
                  </Button>
                )}
                {!app.isFirstParty && (
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`Delete ${app.displayName}`}
                    onClick={() => setDeleting(app)}
                  >
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState icon={AppWindow} title="No applications" className="border-t py-8" />
        )}
      </SettingsSection>
      {creating && (
        <NewApplicationDialog
          onClose={() => setCreating(false)}
          onCreated={(s) => {
            setCreating(false);
            if (s.secret) setSecret(s);
          }}
        />
      )}
      {secret && (
        <Dialog open onOpenChange={(o) => !o && setSecret(undefined)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Secret of {secret.name}</DialogTitle>
            </DialogHeader>
            <div className="flex flex-col gap-3 px-5 pb-4">
              <Alert tone="warning">Copy the client secret now: it is shown only once.</Alert>
              <CopyField label="Client id" value={secret.clientId} />
              <CopyField label="Client secret" value={secret.secret} />
            </div>
            <DialogFooter>
              <Button variant="primary" onClick={() => setSecret(undefined)}>
                Done
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(o) => !o && setDeleting(undefined)}
        title={`Delete ${deleting?.displayName ?? 'the application'}?`}
        description="It can no longer sign anyone in or call the API; its tokens stop working."
        confirm="Delete"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  );
}

function NewApplicationDialog({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (secret: { name: string; clientId: string; secret: string }) => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [clientType, setClientType] = useState('confidential');
  const [grants, setGrants] = useState<string[]>(['authorization_code', 'refresh_token']);
  const [redirects, setRedirects] = useState('');
  const create = useMutation({
    meta: { silent: true },
    mutationFn: async () =>
      (await api.v10.applications.post({
        displayName: name.trim(),
        clientType,
        grantTypes: grants,
        scopes: ['api', 'openid', 'profile', ...(grants.includes('refresh_token') ? ['offline_access'] : [])],
        redirectUris: redirects
          .split('\n')
          .map((r) => r.trim())
          .filter(Boolean),
        postLogoutRedirectUris: [],
      }))!,
    onSuccess: async (result) => {
      toast.success('Application created.');
      await queryClient.invalidateQueries({ queryKey: applicationsQuery.queryKey });
      onCreated({ name: name.trim(), clientId: result.application?.clientId ?? '', secret: result.clientSecret ?? '' });
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };
  const usable = Object.keys(grantLabels).filter((g) => clientType === 'confidential' || g !== 'client_credentials');
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>New application</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-4">
            <Field id="app-name" label="Name">
              <Input id="app-name" required value={name} onChange={(e) => setName(e.target.value)} />
            </Field>
            <Field
              id="app-type"
              label="Type"
              hint="Confidential apps keep a secret on a server; public apps (browser, mobile) cannot."
            >
              <Select
                id="app-type"
                value={clientType}
                onChange={(e) => {
                  setClientType(e.target.value);
                  setGrants(grants.filter((g) => e.target.value === 'confidential' || g !== 'client_credentials'));
                }}
              >
                <option value="confidential">Confidential (server)</option>
                <option value="public">Public (browser or mobile, with PKCE)</option>
              </Select>
            </Field>
            <fieldset className="flex flex-col gap-1">
              <legend className="mb-1 text-[13px] font-medium">May</legend>
              {usable.map((grant) => (
                <label key={grant} className="flex items-center gap-2 text-[13px]">
                  <Checkbox
                    checked={grants.includes(grant)}
                    onChange={(e) =>
                      setGrants(e.target.checked ? [...grants, grant] : grants.filter((g) => g !== grant))
                    }
                  />
                  {grantLabels[grant]}
                </label>
              ))}
            </fieldset>
            {grants.includes('authorization_code') && (
              <Field
                id="app-redirects"
                label="Redirect addresses"
                hint="One per line, e.g. https://app.example.com/callback"
              >
                <Textarea
                  id="app-redirects"
                  required
                  rows={3}
                  className="font-mono text-xs"
                  value={redirects}
                  onChange={(e) => setRedirects(e.target.value)}
                />
              </Field>
            )}
            {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || !grants.length || create.isPending}>
              Create application
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
