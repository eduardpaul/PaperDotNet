import type { QueryClient } from '@tanstack/react-query';
import { createRootRouteWithContext, Link, Outlet } from '@tanstack/react-router';
import { FileQuestion } from 'lucide-react';
import { Toaster } from 'sonner';
import { Button } from '@/components/ui/button';
import { EmptyState } from '@/components/ui/feedback';
import { TooltipProvider } from '@/components/ui/popover';

export interface RouterContext {
  queryClient: QueryClient;
}

export const Route = createRootRouteWithContext<RouterContext>()({
  component: Root,
  notFoundComponent: NotFound,
});

function Root() {
  return (
    <TooltipProvider delayDuration={400}>
      <Outlet />
      <Toaster position="bottom-right" closeButton toastOptions={{ className: 'text-sm' }} />
    </TooltipProvider>
  );
}

function NotFound() {
  return (
    <EmptyState icon={FileQuestion} title="This page does not exist" className="min-h-[60vh]">
      <p className="mb-3">It may have been moved or deleted, or you may not have access to it.</p>
      <Button asChild variant="primary">
        <Link to="/">Go to Home</Link>
      </Button>
    </EmptyState>
  );
}
