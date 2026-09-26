import { jsonNode, jsonOf } from '@paperdotnet/client';
import { createFileRoute, redirect } from '@tanstack/react-router';
import { Fingerprint } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { api, session, signIn } from '@/api/client';
import { Logo } from '@/components/brand/logo';
import { Button } from '@/components/ui/button';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { problemMessage } from '@/lib/errors';

interface LoginSearch {
  /** The authorization request to continue after sign-in (set by the server's /connect/authorize). */
  returnUrl?: string;
  signedOut?: boolean;
}

export const Route = createFileRoute('/login')({
  validateSearch: (search: Record<string, unknown>): LoginSearch => ({
    returnUrl: typeof search.returnUrl === 'string' ? search.returnUrl : undefined,
    signedOut: search.signedOut === true || search.signedOut === 'true' ? true : undefined,
  }),
  beforeLoad: async ({ search }) => {
    if (!search.returnUrl && (await session.isSignedIn())) throw redirect({ to: '/' });
  },
  component: LoginPage,
});

/** Only the server's own authorization endpoint may be continued (no open redirects). */
function safeReturnUrl(returnUrl: string | undefined): string | undefined {
  return returnUrl?.startsWith('/connect/authorize') ? returnUrl : undefined;
}

function LoginPage() {
  const { returnUrl, signedOut } = Route.useSearch();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState<'password' | 'passkey'>();
  const [error, setError] = useState<string>();
  const passkeysSupported = typeof window.PublicKeyCredential?.parseRequestOptionsFromJSON === 'function';

  // The sign-in session exists now; the authorization code flow turns it into tokens for this app.
  const continueSignIn = async () => {
    const next = safeReturnUrl(returnUrl);
    if (next) window.location.assign(next);
    else await signIn('/');
  };

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy('password');
    setError(undefined);
    try {
      await api.v10.auth.login.post({ userName, password });
      await continueSignIn();
    } catch (e) {
      setError(problemMessage(e));
      setBusy(undefined);
    }
  };

  const onPasskey = async () => {
    setBusy('passkey');
    setError(undefined);
    try {
      const options = await api.v10.auth.passkeys.optionsPath.post({ userName: userName || undefined });
      const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(
        jsonOf(options?.options) as PublicKeyCredentialRequestOptionsJSON,
      );
      const credential = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null;
      if (!credential) throw new Error('No passkey was selected.');
      await api.v10.auth.passkeys.login.post({ credential: jsonNode(credential.toJSON()), state: options?.state });
      await continueSignIn();
    } catch (e) {
      setError(
        e instanceof DOMException ? 'The passkey sign-in was cancelled or is not available.' : problemMessage(e),
      );
      setBusy(undefined);
    }
  };

  return (
    <main className="flex min-h-dvh items-center justify-center px-4 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-8 flex justify-center">
          <Logo className="text-lg" />
        </div>
        <div className="rounded-xl border bg-surface p-6 shadow-sm">
          <h1 className="text-lg font-semibold">Sign in</h1>
          <p className="mt-1 mb-5 text-[13px] text-muted">Documents, tasks and calendars in one place.</p>
          {signedOut && !error && (
            <Alert tone="success" className="mb-4">
              You have been signed out.
            </Alert>
          )}
          {error && <Alert className="mb-4">{error}</Alert>}
          <form onSubmit={onSubmit} className="flex flex-col gap-4">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="userName">User name or email</Label>
              <Input
                id="userName"
                autoComplete="username webauthn"
                autoFocus
                required
                value={userName}
                onChange={(e) => setUserName(e.target.value)}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="password">Password</Label>
              <Input
                id="password"
                type="password"
                autoComplete="current-password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>
            <Button type="submit" variant="primary" disabled={busy !== undefined}>
              {busy === 'password' && <Spinner className="text-current" />}
              Sign in
            </Button>
          </form>
          {passkeysSupported && (
            <>
              <div className="my-4 flex items-center gap-3 text-xs text-muted">
                <span className="h-px flex-1 bg-current opacity-20" />
                or
                <span className="h-px flex-1 bg-current opacity-20" />
              </div>
              <Button className="w-full" onClick={onPasskey} disabled={busy !== undefined}>
                {busy === 'passkey' ? <Spinner /> : <Fingerprint />}
                Sign in with a passkey
              </Button>
            </>
          )}
        </div>
      </div>
    </main>
  );
}
