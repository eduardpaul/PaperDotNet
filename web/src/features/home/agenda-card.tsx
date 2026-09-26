import type { CalendarEntry } from '@paperdotnet/client';
import { Link } from '@tanstack/react-router';
import { CalendarDays, CalendarX2, ListChecks, MapPin } from 'lucide-react';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { itemLink } from '@/features/lists/item-link';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';

/** The next days: events and due tasks, grouped by day in the user's time zone. */
export function AgendaCard({ entries, loading }: { entries: CalendarEntry[] | undefined; loading: boolean }) {
  const format = useFormat();
  const now = useNow();
  const today = format.dayKey(now);
  const tomorrow = format.dayKey(new Date(now.getTime() + 86_400_000));
  const days = new Map<string, CalendarEntry[]>();
  for (const entry of [...(entries ?? [])].sort((a, b) => +(a.start ?? 0) - +(b.start ?? 0))) {
    if (!entry.start) continue;
    const key = entry.allDay ? entry.start.toISOString().slice(0, 10) : format.dayKey(entry.start);
    if (key < today) continue;
    days.set(key, [...(days.get(key) ?? []), entry]);
  }

  const label = (key: string) =>
    key === today
      ? 'Today'
      : key === tomorrow
        ? 'Tomorrow'
        : `${format.weekday(key + 'T12:00:00Z')}, ${format.date(key)}`;

  return (
    <Card>
      <CardHeader>
        <CalendarDays className="size-4 text-muted" />
        <CardTitle>Agenda</CardTitle>
      </CardHeader>
      {loading ? (
        <div className="flex flex-col gap-2 p-4">
          <Skeleton className="h-9" />
          <Skeleton className="h-9" />
        </div>
      ) : days.size ? (
        <div className="flex flex-col gap-4 p-4">
          {[...days.entries()].slice(0, 7).map(([key, dayEntries]) => (
            <section key={key}>
              <h3 className="mb-1.5 text-xs font-medium text-muted">{label(key)}</h3>
              <ul className="flex flex-col gap-1.5">
                {dayEntries.map((entry) => (
                  <li
                    key={`${entry.itemId}-${entry.occurrenceStart?.toISOString() ?? ''}`}
                    className="flex gap-3 text-[13px]"
                  >
                    <span className="w-12 shrink-0 text-xs leading-5 text-muted tabular-nums">
                      {entry.allDay || entry.kind === 'task' ? 'All day' : format.time(entry.start)}
                    </span>
                    <Link
                      {...itemLink({ ...entry, itemId: entry.masterItemId ?? entry.itemId })!}
                      className="min-w-0 flex-1 rounded-r border-l-2 border-accent/60 pl-2 hover:bg-surface-muted/60"
                    >
                      <span className="flex items-center gap-1.5 truncate font-medium">
                        {entry.kind === 'task' && <ListChecks className="size-3.5 shrink-0 text-muted" />}
                        {entry.title}
                      </span>
                      {(entry.location || entry.listName) && (
                        <span className="flex items-center gap-1 truncate text-xs text-muted">
                          {entry.location && <MapPin className="size-3" />}
                          {entry.location ?? entry.listName}
                        </span>
                      )}
                    </Link>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      ) : (
        <EmptyState icon={CalendarX2} title="Nothing planned">
          Events and due tasks of the next week appear here.
        </EmptyState>
      )}
    </Card>
  );
}
