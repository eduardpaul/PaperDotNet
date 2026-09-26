import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { addDays, startOfDay } from 'date-fns';
import { useMemo } from 'react';
import { meQuery, myCalendarQuery } from '@/api/queries';
import { Page } from '@/components/page';
import { AgendaCard } from '@/features/home/agenda-card';
import { InboxCard } from '@/features/home/inbox-card';
import { ApprovalsCard } from '@/features/home/approvals-card';
import { TasksCard } from '@/features/home/tasks-card';
import { WorkspacesCard } from '@/features/home/workspaces-card';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';

export const Route = createFileRoute('/_app/')({ component: Home });

function greeting(hour: number) {
  if (hour < 5) return 'Good evening';
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

/** Today first (frontend.md): what needs the user now. */
function Home() {
  const format = useFormat();
  const { data: me } = useQuery(meQuery);
  const range = useMemo(() => {
    const start = startOfDay(new Date());
    return { start, end: addDays(start, 8) };
  }, []);
  const calendar = useQuery(myCalendarQuery(range.start, range.end));
  const now = useNow();
  const firstName = (me?.displayName ?? me?.userName ?? '').split(' ')[0];

  return (
    <Page>
      <header className="mb-6">
        <p className="text-[13px] text-muted">
          {format.weekday(now)}, {format.date(now)}
        </p>
        <h1 className="mt-0.5 text-2xl font-semibold tracking-tight">
          {greeting(format.hour(now))}
          {firstName ? `, ${firstName}` : ''}
        </h1>
      </header>
      <div className="grid gap-4 lg:grid-cols-3">
        <div className="flex flex-col gap-4 lg:col-span-2">
          <TasksCard />
          <ApprovalsCard />
        </div>
        <div className="flex flex-col gap-4">
          <InboxCard />
          <AgendaCard entries={calendar.data} loading={calendar.isPending} />
          <WorkspacesCard />
        </div>
      </div>
    </Page>
  );
}
