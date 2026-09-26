import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Briefcase, ChevronRight } from 'lucide-react';
import { workspacesQuery } from '@/api/queries';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';

export function WorkspacesCard() {
  const { data, isPending } = useQuery(workspacesQuery);
  const workspaces = data?.filter((w) => !w.isPersonal) ?? [];

  return (
    <Card>
      <CardHeader>
        <Briefcase className="size-4 text-muted" />
        <CardTitle className="flex-1">Workspaces</CardTitle>
        <Link to="/w" className="text-xs font-medium text-accent hover:underline">
          All
        </Link>
      </CardHeader>
      {isPending ? (
        <div className="p-4">
          <Skeleton className="h-9" />
        </div>
      ) : workspaces.length ? (
        <ul className="p-1.5">
          {workspaces.slice(0, 6).map((workspace) => (
            <li key={workspace.id}>
              <Link
                to="/w/$workspaceId"
                params={{ workspaceId: workspace.id! }}
                className="flex items-center gap-2 rounded-md px-2.5 py-2 text-[13px] hover:bg-surface-muted"
              >
                <span className="flex-1 truncate font-medium">{workspace.name}</span>
                <ChevronRight className="size-4 text-muted" />
              </Link>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState icon={Briefcase} title="No workspaces yet">
          <Link to="/w" search={{ create: true }} className="font-medium text-accent hover:underline">
            Create a workspace
          </Link>{' '}
          for a team or project.
        </EmptyState>
      )}
    </Card>
  );
}
