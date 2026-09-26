import type { MyTask } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { CircleCheckBig, ListChecks } from 'lucide-react';
import { useState } from 'react';
import { myTasksQuery } from '@/api/queries';
import { Badge } from '@/components/ui/badge';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { itemLink } from '@/features/lists/item-link';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';
import { cn } from '@/lib/utils';

const views = [
  { key: 'overdue', label: 'Overdue' },
  { key: 'dueThisWeek', label: 'This week' },
  { key: 'mine', label: 'All mine' },
] as const;

export function TasksCard() {
  const [view, setView] = useState<(typeof views)[number]['key']>('dueThisWeek');
  const overdue = useQuery(myTasksQuery('overdue'));
  const tasks = useQuery(myTasksQuery(view));

  return (
    <Card>
      <CardHeader className="flex-wrap">
        <ListChecks className="size-4 text-muted" />
        <CardTitle className="flex-1">My tasks</CardTitle>
        <div role="tablist" aria-label="Task view" className="flex rounded-md bg-surface-muted p-0.5">
          {views.map((v) => (
            <button
              key={v.key}
              role="tab"
              type="button"
              aria-selected={view === v.key}
              onClick={() => setView(v.key)}
              className={cn(
                'rounded px-2.5 py-1 text-xs font-medium text-muted',
                view === v.key && 'bg-surface text-foreground shadow-xs',
              )}
            >
              {v.label}
              {v.key === 'overdue' && !!overdue.data?.length && (
                <span className="ml-1 text-danger">{overdue.data.length}</span>
              )}
            </button>
          ))}
        </div>
      </CardHeader>
      {tasks.isPending ? (
        <div className="flex flex-col gap-2 p-4">
          <Skeleton className="h-9" />
          <Skeleton className="h-9" />
          <Skeleton className="h-9" />
        </div>
      ) : tasks.data?.length ? (
        <ul className="divide-y">
          {tasks.data.slice(0, 12).map((task) => (
            <TaskRow key={task.itemId} task={task} />
          ))}
        </ul>
      ) : (
        <EmptyState icon={CircleCheckBig} title={view === 'overdue' ? 'Nothing overdue' : 'No open tasks'}>
          {view === 'overdue' ? 'Well done.' : 'Tasks assigned to you across all task lists appear here.'}
        </EmptyState>
      )}
    </Card>
  );
}

function TaskRow({ task }: { task: MyTask }) {
  const format = useFormat();
  const today = format.dayKey(useNow());
  const due = task.dueDate ?? undefined;
  const tone = !due ? undefined : due < today ? 'danger' : due === today ? 'warning' : 'neutral';

  const link = itemLink(task);
  return (
    <li>
      <Link {...link!} className="flex items-center gap-3 px-4 py-2.5 hover:bg-surface-muted/60">
        <span
          aria-label={`Priority ${task.priority ?? 'normal'}`}
          className={cn(
            'size-2 shrink-0 rounded-full',
            task.priority === 'high' ? 'bg-danger' : task.priority === 'low' ? 'bg-border' : 'bg-accent/60',
          )}
        />
        <div className="min-w-0 flex-1">
          <p className="truncate text-[13px] font-medium">{task.title}</p>
          <p className="truncate text-xs text-muted">
            {task.listName}
            {task.status === 'inProgress' && ' · In progress'}
          </p>
        </div>
        {due && <Badge tone={tone}>{due === today ? 'Today' : format.date(due)}</Badge>}
      </Link>
    </li>
  );
}
