import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { completeSignIn } from '@/api/client';
import { Logo } from '@/components/brand/logo';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Button } from '@/components/ui/button';

export const Route = createFileRoute('/callback')({ component: Callback });

/** Where the server returns after sign-in (with a code) and after sign-out (without one). */
function Callback() {
  const navigate = useNavigate();
  const [error, setError] = useState<string>();
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    const parameters = new URLSearchParams(window.location.search);
    if (!parameters.has('code') && !parameters.has('error')) {
      void navigate({ to: '/login', search: { signedOut: true }, replace: true });
      return;
    }

    completeSignIn().then(
      (returnTo) => window.location.replace(returnTo),
      () => setError('The sign-in could not be completed. Please try again.'),
    );
  }, [navigate]);

  return (
    <main className="flex min-h-dvh flex-col items-center justify-center gap-6 px-4">
      <Logo className="text-lg" />
      {error ? (
        <div className="flex w-full max-w-sm flex-col gap-3">
          <Alert>{error}</Alert>
          <Button variant="primary" onClick={() => void navigate({ to: '/login' })}>
            Back to sign-in
          </Button>
        </div>
      ) : (
        <p className="flex items-center gap-2 text-muted">
          <Spinner /> Signing you in…
        </p>
      )}
    </main>
  );
}
