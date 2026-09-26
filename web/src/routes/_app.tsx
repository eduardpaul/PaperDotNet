import { createFileRoute, Outlet } from '@tanstack/react-router';
import { session, signIn } from '@/api/client';
import { meQuery } from '@/api/queries';
import { AppShell } from '@/components/shell/app-shell';
import { useLiveEvents } from '@/lib/live-events';
import { PreferencesProvider } from '@/lib/preferences';

/** Every signed-in screen: the shell, the user's preferences and live updates. */
export const Route = createFileRoute('/_app')({
  beforeLoad: async ({ location }) => {
    if (!(await session.isSignedIn())) await signIn(location.href);
  },
  loader: ({ context }) => context.queryClient.ensureQueryData(meQuery),
  component: SignedIn,
});

function SignedIn() {
  useLiveEvents();
  return (
    <PreferencesProvider>
      <AppShell>
        <Outlet />
      </AppShell>
    </PreferencesProvider>
  );
}
