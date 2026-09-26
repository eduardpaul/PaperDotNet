import type { ApiTokenResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { KeyRound, Plus, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { meQuery } from '@/api/queries';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { ConfirmDialog, CopyField, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';

export const Route = createFileRoute('/_app/settings/tokens')({ component: ApiTokens });

const tokensQuery = {
  queryKey: ['me', 'apiTokens'],
  queryFn: async () => (await api.v10.me.apiTokens.get()) ?? [],
};

const scopesQuery = {
  queryKey: ['scopes'],
  queryFn: async () => (await api.v10.scopes.get()) ?? [],
  staleTime: Infinity,
};

/** Personal API tokens for scripts, the CLI and integrations (IAM-03): only scopes the user holds. */
function ApiTokens() {
  const format = useFormat();
  const now = useNow();
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(tokensQuery);
  const [creating, setCreating] = useState(false);
  const [revoking, setRevoking] = useState<ApiTokenResponse>();
  const revoke = useMutation({
    meta: { silent: true },
    mutationFn: (token: ApiTokenResponse) => api.v10.me.apiTokens.byId(token.id!).delete(),
    onSuccess: async () => {
      setRevoking(undefined);
      toast.success('Token revoked.');
      await queryClient.invalidateQueries({ queryKey: tokensQuery.queryKey });
    },
  });

  return (
    <SettingsSection
      title="API tokens"
      description={
        <>
          For scripts, the command-line client and integrations: send it as{' '}
          <code className="rounded bg-surface-muted px-1">Authorization: Bearer …</code>. A token can do only what its
          scopes allow, and never more than you can.
        </>
      }
      className="px-0 pb-0"
      actions={
        <Button variant="primary" onClick={() => setCreating(true)}>
          <Plus /> New token
        </Button>
      }
    >
      {isPending ? (
        <Skeleton className="mx-5 mb-5 h-12" />
      ) : data?.length ? (
        <ul className="divide-y border-t">
          {data.map((token) => {
            const expired = !!token.expiresAt && token.expiresAt < now;
            return (
              <li key={token.id} className="flex items-start gap-3 px-5 py-3">
                <KeyRound className="mt-0.5 size-4 text-muted" />
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 text-[13px] font-medium">
                    <span className="truncate">{token.name}</span>
                    <code className="text-xs font-normal text-muted">{token.prefix}…</code>
                    {expired && <Badge tone="danger">Expired</Badge>}
                  </p>
                  <p className="text-xs text-muted">
                    Created {format.date(token.createdAt)} ·{' '}
                    {token.expiresAt
                      ? `${expired ? 'expired' : 'expires'} ${format.date(token.expiresAt)}`
                      : 'never expires'}{' '}
                    · {token.lastUsedAt ? `last used ${format.relative(token.lastUsedAt, now)}` : 'never used'}
                  </p>
                  <div className="mt-1.5 flex flex-wrap gap-1">
                    {token.scopes?.map((scope) => (
                      <Badge key={scope}>{scope}</Badge>
                    ))}
                  </div>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label={`Revoke ${token.name}`}
                  onClick={() => setRevoking(token)}
                >
                  <Trash2 />
                </Button>
              </li>
            );
          })}
        </ul>
      ) : (
        <EmptyState icon={KeyRound} title="No API tokens" className="border-t py-6" />
      )}
      {creating && <NewTokenDialog onClose={() => setCreating(false)} />}
      <ConfirmDialog
        open={!!revoking}
        onOpenChange={(open) => !open && setRevoking(undefined)}
        title="Revoke this token?"
        description={`Scripts and integrations using “${revoking?.name ?? ''}” stop working at once.`}
        confirm="Revoke"
        busy={revoke.isPending}
        error={revoke.error}
        onConfirm={() => revoking && revoke.mutate(revoking)}
      />
    </SettingsSection>
  );
}

function NewTokenDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const { data: me } = useQuery(meQuery);
  const { data: catalog } = useQuery(scopesQuery);
  const held = me?.scopes ?? [];
  const description = new Map((catalog ?? []).map((s) => [s.name, s.description]));
  const [name, setName] = useState('');
  const [days, setDays] = useState('90');
  const [scopes, setScopes] = useState<string[]>([]);
  const create = useMutation({
    meta: { silent: true },
    mutationFn: async () =>
      (await api.v10.me.apiTokens.post({
        name: name.trim(),
        scopes,
        expiresInDays: days ? Number(days) : undefined,
      }))!,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: tokensQuery.queryKey }),
  });
  const toggle = (scope: string, on: boolean) => setScopes(on ? [...scopes, scope] : scopes.filter((s) => s !== scope));
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };
  const secret = create.data?.secret;

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-xl">
        {secret ? (
          <>
            <DialogHeader>
              <DialogTitle>Token created</DialogTitle>
            </DialogHeader>
            <div className="flex flex-col gap-3 px-5 pb-4">
              <Alert tone="warning">Copy the token now: it is shown only once.</Alert>
              <CopyField label="API token" value={secret} />
            </div>
            <DialogFooter>
              <Button variant="primary" onClick={onClose}>
                Done
              </Button>
            </DialogFooter>
          </>
        ) : (
          <form onSubmit={onSubmit}>
            <DialogHeader>
              <DialogTitle>New API token</DialogTitle>
            </DialogHeader>
            <div className="flex max-h-[60vh] flex-col gap-4 overflow-y-auto px-5 pb-4">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="token-name">Name</Label>
                <Input
                  id="token-name"
                  placeholder="e.g. Backup script"
                  maxLength={200}
                  value={name}
                  autoFocus
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="token-expiry">Expires</Label>
                <Select id="token-expiry" value={days} onChange={(e) => setDays(e.target.value)}>
                  <option value="30">In 30 days</option>
                  <option value="90">In 90 days</option>
                  <option value="365">In a year</option>
                  <option value="">Never</option>
                </Select>
              </div>
              <fieldset className="flex flex-col gap-1.5">
                <legend className="mb-1.5 flex w-full items-center text-[13px] font-medium">
                  <span className="flex-1">Scopes</span>
                  <Button
                    type="button"
                    variant="link"
                    size="sm"
                    className="h-auto px-0"
                    onClick={() => setScopes(held.filter((s) => s.endsWith('.read')))}
                  >
                    All read scopes
                  </Button>
                </legend>
                <ul className="flex flex-col gap-1 rounded-md border p-2">
                  {held.map((scope) => (
                    <li key={scope}>
                      <label className="flex items-start gap-2 rounded px-1 py-1 text-[13px] hover:bg-surface-muted">
                        <Checkbox
                          className="mt-0.5"
                          checked={scopes.includes(scope)}
                          onChange={(e) => toggle(scope, e.target.checked)}
                        />
                        <span>
                          <code className="text-xs">{scope}</code>
                          {description.get(scope) && (
                            <span className="block text-xs text-muted">{description.get(scope)}</span>
                          )}
                        </span>
                      </label>
                    </li>
                  ))}
                </ul>
              </fieldset>
              {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
            </div>
            <DialogFooter>
              <Button type="button" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={!name.trim() || !scopes.length || create.isPending}>
                Create token
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}
