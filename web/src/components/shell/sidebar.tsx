import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';
import { Briefcase, ChevronRight, Plus } from 'lucide-react';
import { Collapsible } from 'radix-ui';
import { useState } from 'react';
import { listsQuery, unreadCountQuery, workspacesQuery } from '@/api/queries';
import { Logo } from '@/components/brand/logo';
import { Skeleton } from '@/components/ui/feedback';
import { navigation } from '@/extensibility/navigation';
import { ListIcon } from '@/features/lists/list-icon';
import { cn } from '@/lib/utils';

const itemClass =
  'flex h-8 items-center gap-2.5 rounded-md px-2 text-[13px] text-foreground/80 hover:bg-surface-muted hover:text-foreground [&_svg]:size-4 [&_svg]:shrink-0 [&_svg]:text-muted';
const activeClass = 'bg-accent-soft! text-accent! font-medium [&_svg]:text-accent!';

export function Sidebar({ onNavigate }: { onNavigate?: () => void }) {
  const { data: unread } = useQuery(unreadCountQuery);

  return (
    <nav aria-label="Main" className="flex h-full flex-col border-r bg-sidebar">
      <div className="flex h-14 items-center px-4">
        <Link to="/" onClick={onNavigate}>
          <Logo />
        </Link>
      </div>
      <div className="flex-1 overflow-y-auto px-2 pb-4">
        <ul className="flex flex-col gap-0.5">
          {navigation.map((entry) => (
            <li key={entry.to}>
              <Link
                to={entry.to}
                onClick={onNavigate}
                className={itemClass}
                activeProps={{ className: activeClass }}
                activeOptions={{ exact: entry.exact }}
              >
                <entry.icon />
                <span className="flex-1">{entry.label}</span>
                {entry.to === '/notifications' && !!unread && (
                  <span className="rounded-full bg-accent px-1.5 text-[11px] leading-4 font-semibold text-accent-foreground">
                    {unread > 99 ? '99+' : unread}
                  </span>
                )}
              </Link>
            </li>
          ))}
        </ul>
        <Workspaces onNavigate={onNavigate} />
      </div>
    </nav>
  );
}

/** The lists of the open workspace, under it in the sidebar. */
function WorkspaceLists({ workspaceId, onNavigate }: { workspaceId: string; onNavigate?: () => void }) {
  const { data } = useQuery(listsQuery(workspaceId));
  return (
    <ul className="mt-0.5 mb-1 ml-4 flex flex-col gap-0.5 border-l pl-2">
      {data?.map((list) => (
        <li key={list.id}>
          <Link
            to="/w/$workspaceId/l/$listId"
            params={{ workspaceId, listId: list.id! }}
            onClick={onNavigate}
            className={itemClass}
            activeProps={{ className: activeClass }}
          >
            <ListIcon list={list} />
            <span className="truncate">{list.name}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

function Workspaces({ onNavigate }: { onNavigate?: () => void }) {
  const { data, isPending } = useQuery(workspacesQuery);
  const { workspaceId: current } = useParams({ strict: false });
  const [open, setOpen] = useState(true);

  return (
    <Collapsible.Root open={open} onOpenChange={setOpen} className="mt-5">
      <div className="flex items-center px-2 pb-1">
        <Collapsible.Trigger className="flex flex-1 items-center gap-1 text-xs font-medium text-muted hover:text-foreground">
          <ChevronRight className={cn('size-3 transition-transform', open && 'rotate-90')} />
          Workspaces
        </Collapsible.Trigger>
        <Link
          to="/w"
          search={{ create: true }}
          onClick={onNavigate}
          aria-label="New workspace"
          className="rounded p-0.5 text-muted hover:bg-surface-muted hover:text-foreground"
        >
          <Plus className="size-3.5" />
        </Link>
      </div>
      <Collapsible.Content>
        <ul className="flex flex-col gap-0.5">
          {isPending &&
            [0, 1, 2].map((i) => (
              <li key={i} className="px-2 py-1.5">
                <Skeleton className="h-4 w-3/4" />
              </li>
            ))}
          {data
            ?.filter((w) => !w.isPersonal)
            .map((workspace) => (
              <li key={workspace.id}>
                <Link
                  to="/w/$workspaceId"
                  params={{ workspaceId: workspace.id! }}
                  onClick={onNavigate}
                  className={itemClass}
                  activeProps={{ className: activeClass }}
                  activeOptions={{ exact: true }}
                >
                  <Briefcase />
                  <span className="truncate">{workspace.name}</span>
                </Link>
                {current && workspace.id === current && (
                  <WorkspaceLists workspaceId={current} onNavigate={onNavigate} />
                )}
              </li>
            ))}
        </ul>
      </Collapsible.Content>
    </Collapsible.Root>
  );
}
