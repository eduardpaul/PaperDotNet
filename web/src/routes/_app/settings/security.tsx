import type { PasskeyResponse } from '@paperdotnet/client';
import { jsonNode, jsonOf, validationErrors } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { Fingerprint, Plus, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { ConfirmDialog, SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/settings/security')({ component: Security });

function Security() {
  return (
    <>
      <ChangePassword />
      <Passkeys />
    </>
  );
}

/** Changing the own password needs the current one (IAM-14). */
function ChangePassword() {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const change = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.me.password.post({ currentPassword: current, newPassword: next }),
    onSuccess: () => {
      setCurrent('');
      setNext('');
      setConfirm('');
      toast.success('Password changed. Other devices have to sign in again.');
    },
  });
  const mismatch = confirm.length > 0 && next !== confirm;
  const errors = change.isError ? validationErrors(change.error) : {};
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (!mismatch) change.mutate();
  };
  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Password"
        description="Use a long password you do not use anywhere else."
        actions={
          <Button type="submit" variant="primary" disabled={!current || !next || next !== confirm || change.isPending}>
            Change password
          </Button>
        }
      >
        {/* Lets password managers attach the new password to the right account. */}
        <input type="text" name="username" autoComplete="username" hidden readOnly />
        <div className="divide-y">
          <SettingRow id="current-password" label="Current password">
            <Input
              id="current-password"
              type="password"
              autoComplete="current-password"
              value={current}
              aria-invalid={!!errors.currentPassword || undefined}
              onChange={(e) => setCurrent(e.target.value)}
            />
          </SettingRow>
          <SettingRow id="new-password" label="New password">
            <Input
              id="new-password"
              type="password"
              autoComplete="new-password"
              value={next}
              aria-invalid={!!errors.newPassword || undefined}
              onChange={(e) => setNext(e.target.value)}
            />
          </SettingRow>
          <SettingRow id="confirm-password" label="Repeat new password">
            <Input
              id="confirm-password"
              type="password"
              autoComplete="new-password"
              value={confirm}
              aria-invalid={mismatch || undefined}
              onChange={(e) => setConfirm(e.target.value)}
            />
            {mismatch && <p className="text-xs text-danger">The passwords are not the same.</p>}
          </SettingRow>
        </div>
        {change.isError && <Alert className="mt-3">{problemMessage(change.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}

const passkeysQuery = {
  queryKey: ['me', 'passkeys'],
  queryFn: async () => (await api.v10.me.passkeys.get()) ?? [],
};

/** Passkeys sign in without a password (IAM-01): Face ID, Windows Hello, a security key or a password manager. */
function Passkeys() {
  const format = useFormat();
  const { data, isPending } = useQuery(passkeysQuery);
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<PasskeyResponse>();
  const queryClient = useQueryClient();
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (passkey: PasskeyResponse) => api.v10.me.passkeys.byId(passkey.id!).delete(),
    onSuccess: async () => {
      setRemoving(undefined);
      toast.success('Passkey removed.');
      await queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey });
    },
  });
  const supported = typeof window.PublicKeyCredential?.parseCreationOptionsFromJSON === 'function';

  return (
    <SettingsSection
      title="Passkeys"
      description="Sign in with your fingerprint, face or a security key instead of a password."
      className="px-0 pb-0"
      actions={
        supported ? (
          <Button onClick={() => setAdding(true)}>
            <Plus /> Add a passkey
          </Button>
        ) : (
          <span className="text-xs text-muted">This browser cannot create passkeys.</span>
        )
      }
    >
      {isPending ? (
        <Skeleton className="mx-5 mb-5 h-12" />
      ) : data?.length ? (
        <ul className="divide-y border-t">
          {data.map((passkey) => (
            <li key={passkey.id} className="flex items-center gap-3 px-5 py-3">
              <Fingerprint className="size-5 text-muted" />
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13px] font-medium">{passkey.name || 'Passkey'}</p>
                <p className="text-xs text-muted">Added {format.date(passkey.createdAt)}</p>
              </div>
              {passkey.isBackedUp && <Badge>Synced</Badge>}
              <Button
                variant="ghost"
                size="icon"
                aria-label={`Remove ${passkey.name || 'passkey'}`}
                onClick={() => setRemoving(passkey)}
              >
                <Trash2 />
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState icon={Fingerprint} title="No passkeys yet" className="border-t py-6" />
      )}
      {adding && <AddPasskeyDialog onClose={() => setAdding(false)} />}
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(open) => !open && setRemoving(undefined)}
        title="Remove this passkey?"
        description={`“${removing?.name || 'Passkey'}” will no longer sign you in. Remove it from the device or password manager too.`}
        confirm="Remove"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </SettingsSection>
  );
}

function AddPasskeyDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const add = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const options = await api.v10.me.passkeys.optionsPath.post();
      const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(
        jsonOf(options?.options) as PublicKeyCredentialCreationOptionsJSON,
      );
      const credential = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
      if (!credential) throw new Error('No passkey was created.');
      await api.v10.me.passkeys.post({
        name: name.trim() || undefined,
        credential: jsonNode(credential.toJSON()),
        state: options?.state,
      });
    },
    onSuccess: async () => {
      toast.success('Passkey added. You can sign in with it now.');
      await queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey });
      onClose();
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate();
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>Add a passkey</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-2 px-5 pb-4">
            <Label htmlFor="passkey-name">Name</Label>
            <Input
              id="passkey-name"
              placeholder="e.g. Work laptop"
              maxLength={100}
              value={name}
              autoFocus
              onChange={(e) => setName(e.target.value)}
            />
            <p className="text-xs text-muted">Your browser asks next where to keep the passkey.</p>
            {add.isError && (
              <Alert>
                {add.error instanceof DOMException
                  ? 'The passkey was not created (cancelled, or this device already has one for you).'
                  : problemMessage(add.error)}
              </Alert>
            )}
          </div>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={add.isPending}>
              {add.isPending ? <Spinner /> : <Fingerprint />} Continue
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
