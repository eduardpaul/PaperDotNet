import { isStatus, problemOf } from '@paperdotnet/client';
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createRouter, RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { toast } from 'sonner';
import { signIn } from './api/client';
import { problemMessage } from './lib/errors';
import { routeTree } from './routeTree.gen';
import './styles.css';

function onError(error: unknown) {
  // The client already refreshed once; a 401 now means the session is over.
  if (isStatus(error, 401)) void signIn();
}

const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError }),
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      onError(error);
      // Screens that show errors themselves (forms) set meta.silent.
      if (!mutation.meta?.silent && !isStatus(error, 401)) toast.error(problemMessage(error));
    },
  }),
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: true,
      // Client errors (not found, forbidden, invalid) do not get better by retrying.
      retry: (count, error) => count < 2 && !(problemOf(error)?.responseStatusCode ?? 0).toString().startsWith('4'),
    },
  },
});

const router = createRouter({
  routeTree,
  context: { queryClient },
  defaultPreload: 'intent',
  defaultPreloadStaleTime: 0,
  scrollRestoration: true,
});

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}

declare module '@tanstack/react-query' {
  interface Register {
    mutationMeta: { silent?: boolean };
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
