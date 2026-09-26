import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { Briefcase, Plus } from 'lucide-react';
import { workspacesQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { CreateWorkspaceDialog } from '@/features/workspaces/create-workspace-dialog';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/w/')({
  validateSearch: (search: Record<string, unknown>): { create?: boolean } => ({
    create: search.create === true || search.create === 'true' ? true : undefined,
  }),
  component: Workspaces,
});

function Workspaces() {
  const { create } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const format = useFormat();
  const { data, isPending } = useQuery(workspacesQuery);
  const workspaces = data?.filter((w) => !w.isPersonal) ?? [];
  const setCreate = (open: boolean) => void navigate({ search: open ? { create: true } : {}, replace: true });

  return (
    <Page>
      <PageHeader
        title="Workspaces"
        description="Teams and projects, each with its own members, lists and libraries."
        actions={
          <Button variant="primary" onClick={() => setCreate(true)}>
            <Plus /> New workspace
          </Button>
        }
      />
      {isPending ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Skeleton className="h-28" />
          <Skeleton className="h-28" />
          <Skeleton className="h-28" />
        </div>
      ) : workspaces.length ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {workspaces.map((workspace) => (
            <Link key={workspace.id} to="/w/$workspaceId" params={{ workspaceId: workspace.id! }} className="group">
              <Card className="flex h-full flex-col gap-2 p-4 transition-colors group-hover:border-accent/50">
                <div className="flex items-center gap-2">
                  <span className="rounded-md bg-accent-soft p-1.5 text-accent">
                    <Briefcase className="size-4" />
                  </span>
                  <h2 className="truncate font-semibold">{workspace.name}</h2>
                </div>
                <p className="line-clamp-2 flex-1 text-[13px] text-muted">
                  {workspace.description || 'No description'}
                </p>
                <p className="text-xs text-muted">Updated {format.relative(workspace.updatedAt)}</p>
              </Card>
            </Link>
          ))}
        </div>
      ) : (
        <Card>
          <EmptyState icon={Briefcase} title="No workspaces yet">
            <p className="mb-3">Create one for a team or project to add lists and libraries.</p>
            <Button variant="primary" onClick={() => setCreate(true)}>
              <Plus /> New workspace
            </Button>
          </EmptyState>
        </Card>
      )}
      <CreateWorkspaceDialog open={!!create} onOpenChange={setCreate} />
    </Page>
  );
}
