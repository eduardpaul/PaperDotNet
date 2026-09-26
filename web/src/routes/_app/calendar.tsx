import type { CalendarEntry } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import {
  addDays,
  addMonths,
  eachDayOfInterval,
  endOfMonth,
  endOfWeek,
  format as formatDate,
  parseISO,
  startOfMonth,
  startOfWeek,
} from 'date-fns';
import { CalendarDays, ChevronLeft, ChevronRight, ListChecks, MapPin, Plus, Repeat, Upload } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { myCalendarQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Select } from '@/components/ui/select';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { FilePickerButton } from '@/features/documents/drop-zone';
import { ItemPanelLoader } from '@/features/lists/item-panel-loader';
import { rememberedList, rememberList, useListsByTemplate } from '@/features/lists/lists-by-template';
import { listBuilder } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';
import { fromZonedInput } from '@/lib/zoned';
import { cn } from '@/lib/utils';

interface CalendarSearch {
  view?: 'agenda';
  /** A day in the month shown (yyyy-MM-dd); today when omitted. */
  date?: string;
  /** The open item: "workspaceId/listId/itemId" ("new" as item for a new event). */
  open?: string;
  /** A new event's day. */
  day?: string;
  tab?: string;
}

const isDay = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);

export const Route = createFileRoute('/_app/calendar')({
  validateSearch: (search: Record<string, unknown>): CalendarSearch => ({
    view: search.view === 'agenda' ? 'agenda' : undefined,
    date: isDay(search.date) ? search.date : undefined,
    open: typeof search.open === 'string' && search.open.split('/').length === 3 ? search.open : undefined,
    day: isDay(search.day) ? search.day : undefined,
    tab: typeof search.tab === 'string' ? search.tab : undefined,
  }),
  component: CalendarPage,
});

/** Events and due tasks of every calendar and task list (CAL-01…04), by month or as an agenda. */
function CalendarPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const format = useFormat();
  const zone = format.preferences.timeZone;
  const today = format.dayKey(useNow());
  const anchor = parseISO(search.date ?? today);
  const calendars = useListsByTemplate('calendar');
  const [choose, setChoose] = useState<string>();
  const setSearch = (patch: Partial<CalendarSearch>, replace = false) =>
    void navigate({ search: (c) => ({ ...c, ...patch }), replace });

  const agenda = search.view === 'agenda';
  const first = agenda ? anchor : startOfWeek(startOfMonth(anchor), { weekStartsOn: 1 });
  const last = agenda ? addDays(anchor, 30) : endOfWeek(endOfMonth(anchor), { weekStartsOn: 1 });
  const days = eachDayOfInterval({ start: first, end: last }).map((d) => formatDate(d, 'yyyy-MM-dd'));
  const start = new Date(fromZonedInput(`${days[0]}T00:00`, zone)!);
  const end = new Date(fromZonedInput(`${formatDate(addDays(last, 1), 'yyyy-MM-dd')}T00:00`, zone)!);
  const { data, isPending } = useQuery(myCalendarQuery(start, end));

  const byDay = new Map<string, CalendarEntry[]>();
  for (const entry of [...(data ?? [])].sort((a, b) => +(a.start ?? 0) - +(b.start ?? 0))) {
    if (!entry.start) continue;
    const key =
      entry.allDay || entry.kind === 'task' ? entry.start.toISOString().slice(0, 10) : format.dayKey(entry.start);
    byDay.set(key, [...(byDay.get(key) ?? []), entry]);
  }

  const newEvent = (day: string) => {
    const target =
      calendars.find((c) => c.id === rememberedList('eventCalendar')) ??
      (calendars.length === 1 ? calendars[0] : undefined);
    if (target) setSearch({ open: `${target.workspaceId}/${target.id}/new`, day });
    else setChoose(day);
  };
  const [openWs, openList, openItem] = search.open?.split('/') ?? [];
  const move = (months: number) =>
    setSearch({ date: formatDate(agenda ? addDays(anchor, months * 30) : addMonths(anchor, months), 'yyyy-MM-dd') });

  return (
    <Page wide className="max-w-[1400px]">
      <PageHeader
        icon={CalendarDays}
        title={
          agenda
            ? 'Agenda'
            : new Intl.DateTimeFormat(format.preferences.language, { month: 'long', year: 'numeric' }).format(anchor)
        }
        actions={
          <>
            <ImportButton calendars={calendars} />
            <Button variant="primary" disabled={!calendars.length} onClick={() => newEvent(search.date ?? today)}>
              <Plus /> New event
            </Button>
          </>
        }
      />
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Button size="icon" aria-label="Previous" onClick={() => move(-1)}>
          <ChevronLeft />
        </Button>
        <Button onClick={() => setSearch({ date: undefined })}>Today</Button>
        <Button size="icon" aria-label="Next" onClick={() => move(1)}>
          <ChevronRight />
        </Button>
        <div role="tablist" aria-label="Calendar view" className="ml-auto flex rounded-md bg-surface-muted p-0.5">
          {[
            { key: undefined, label: 'Month' },
            { key: 'agenda' as const, label: 'Agenda' },
          ].map((v) => (
            <button
              key={v.label}
              role="tab"
              type="button"
              aria-selected={search.view === v.key}
              onClick={() => setSearch({ view: v.key })}
              className={cn(
                'rounded px-2.5 py-1 text-xs font-medium text-muted',
                search.view === v.key && 'bg-surface text-foreground shadow-xs',
              )}
            >
              {v.label}
            </button>
          ))}
        </div>
      </div>

      {isPending ? (
        <Skeleton className="h-[480px]" />
      ) : agenda ? (
        <Card>
          {days.filter((d) => byDay.has(d)).length ? (
            <ol className="divide-y">
              {days
                .filter((d) => byDay.has(d))
                .map((day) => (
                  <li key={day} className="flex gap-4 px-4 py-3">
                    <div
                      className={cn(
                        'w-24 shrink-0 text-xs',
                        day === today ? 'font-semibold text-accent' : 'text-muted',
                      )}
                    >
                      {format.weekday(`${day}T12:00:00Z`, 'short')}, {format.date(day)}
                    </div>
                    <ul className="flex min-w-0 flex-1 flex-col gap-1">
                      {byDay.get(day)!.map((entry) => (
                        <EntryChip
                          key={`${entry.itemId}-${entry.occurrenceStart?.toISOString() ?? ''}`}
                          entry={entry}
                          wide
                          onOpen={(open) => setSearch({ open })}
                        />
                      ))}
                    </ul>
                  </li>
                ))}
            </ol>
          ) : (
            <EmptyState icon={CalendarDays} title="Nothing in the next 30 days" />
          )}
        </Card>
      ) : (
        <div className="overflow-hidden rounded-lg border bg-surface">
          <div className="grid grid-cols-7 border-b text-center text-xs font-medium text-muted">
            {days.slice(0, 7).map((d) => (
              <div key={d} className="py-2">
                {format.weekday(`${d}T12:00:00Z`, 'short')}
              </div>
            ))}
          </div>
          <div className="grid grid-cols-7">
            {days.map((day) => {
              const entries = byDay.get(day) ?? [];
              const inMonth = day.slice(0, 7) === formatDate(anchor, 'yyyy-MM');
              return (
                <div
                  key={day}
                  role="gridcell"
                  aria-label={format.date(day)}
                  className={cn(
                    'group min-h-28 border-r border-b p-1.5 [&:nth-child(7n)]:border-r-0',
                    !inMonth && 'bg-surface-muted/40',
                  )}
                >
                  <div className="mb-1 flex items-center justify-between">
                    <span
                      className={cn(
                        'flex size-6 items-center justify-center rounded-full text-xs',
                        day === today ? 'bg-accent font-semibold text-accent-foreground' : inMonth ? '' : 'text-muted',
                      )}
                    >
                      {Number(day.slice(8))}
                    </span>
                    <button
                      type="button"
                      aria-label={`New event on ${format.date(day)}`}
                      disabled={!calendars.length}
                      onClick={() => newEvent(day)}
                      className="rounded p-0.5 text-muted opacity-0 group-hover:opacity-100 hover:bg-surface-muted focus-visible:opacity-100"
                    >
                      <Plus className="size-3.5" />
                    </button>
                  </div>
                  <ul className="flex flex-col gap-0.5">
                    {entries.slice(0, 3).map((entry) => (
                      <EntryChip
                        key={`${entry.itemId}-${entry.occurrenceStart?.toISOString() ?? ''}`}
                        entry={entry}
                        onOpen={(open) => setSearch({ open })}
                      />
                    ))}
                    {entries.length > 3 && (
                      <li>
                        <button
                          type="button"
                          className="px-1 text-[11px] text-muted hover:underline"
                          onClick={() => setSearch({ view: 'agenda', date: day })}
                        >
                          +{entries.length - 3} more
                        </button>
                      </li>
                    )}
                  </ul>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {openWs && openList && openItem && (
        <ItemPanelLoader
          workspaceId={openWs}
          listId={openList}
          itemId={openItem}
          tab={search.tab ?? 'details'}
          initialValues={
            openItem === 'new' && search.day
              ? { start: fromZonedInput(`${search.day}T09:00`, zone), end: fromZonedInput(`${search.day}T10:00`, zone) }
              : undefined
          }
          onTab={(tab) => setSearch({ tab }, true)}
          onCreated={(id) => {
            rememberList('eventCalendar', openList);
            setSearch({ open: `${openWs}/${openList}/${id}`, day: undefined }, true);
          }}
          onClose={() => setSearch({ open: undefined, tab: undefined, day: undefined })}
        />
      )}
      {choose && (
        <Dialog open onOpenChange={() => setChoose(undefined)}>
          <DialogContent className="max-w-sm">
            <DialogHeader>
              <DialogTitle>Which calendar?</DialogTitle>
            </DialogHeader>
            <ul className="flex flex-col gap-1 px-5 pb-5">
              {calendars.map((c) => (
                <li key={c.id}>
                  <Button
                    className="w-full justify-start"
                    onClick={() => {
                      rememberList('eventCalendar', c.id!);
                      setChoose(undefined);
                      setSearch({ open: `${c.workspaceId}/${c.id}/new`, day: choose });
                    }}
                  >
                    {c.workspaceName} › {c.name}
                  </Button>
                </li>
              ))}
            </ul>
          </DialogContent>
        </Dialog>
      )}
    </Page>
  );
}

/** An event or due task; opens a summary with actions (open; skip one occurrence of a series). */
function EntryChip({ entry, wide, onOpen }: { entry: CalendarEntry; wide?: boolean; onOpen: (open: string) => void }) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const itemId = entry.masterItemId ?? entry.itemId;
  const skip = useMutation({
    mutationFn: () =>
      listBuilder(entry.workspaceId!, entry.listId!)
        .items.byItemId(itemId!)
        .series.occurrences.byOccurrenceStart(entry.occurrenceStart!)
        .delete(),
    onSuccess: async () => {
      toast.success('This occurrence is skipped.');
      await queryClient.invalidateQueries({ queryKey: ['me', 'calendar'] });
    },
  });
  const time = entry.allDay || entry.kind === 'task' ? '' : format.time(entry.start);
  const task = entry.kind === 'task';

  return (
    <li>
      <Popover>
        <PopoverTrigger
          className={cn(
            'flex w-full items-center gap-1 truncate rounded px-1.5 py-0.5 text-left text-[11px]',
            task ? 'bg-surface-muted text-foreground' : 'bg-accent-soft text-accent',
            wide && 'py-1.5 text-[13px]',
            entry.status === 'completed' && 'line-through opacity-60',
          )}
        >
          {task && <ListChecks className="size-3 shrink-0" />}
          {entry.recurring && <Repeat className="size-3 shrink-0" />}
          {time && <span className="shrink-0 tabular-nums opacity-80">{time}</span>}
          <span className="truncate font-medium">{entry.title}</span>
          {wide && entry.location && <span className="truncate text-muted">· {entry.location}</span>}
        </PopoverTrigger>
        <PopoverContent align="start" className="w-72 p-4">
          <p className="font-semibold">{entry.title}</p>
          <p className="mt-1 text-xs text-muted">
            {entry.allDay || task
              ? format.date(entry.start)
              : `${format.dateTime(entry.start)}${entry.end ? ` – ${format.time(entry.end)}` : ''}`}
            {entry.listName && <> · {entry.listName}</>}
          </p>
          {entry.location && (
            <p className="mt-1 flex items-center gap-1 text-xs">
              <MapPin className="size-3" /> {entry.location}
            </p>
          )}
          <div className="mt-3 flex gap-2">
            <Button
              size="sm"
              variant="primary"
              onClick={() => onOpen(`${entry.workspaceId}/${entry.listId}/${itemId}`)}
            >
              Open{entry.recurring ? ' series' : ''}
            </Button>
            {entry.recurring && entry.occurrenceStart && (
              <Button size="sm" disabled={skip.isPending} onClick={() => skip.mutate()}>
                Skip this one
              </Button>
            )}
          </div>
        </PopoverContent>
      </Popover>
    </li>
  );
}

/** Imports an .ics file into a calendar (CAL-04); events with the same UID are updated, not duplicated. */
function ImportButton({ calendars }: { calendars: ReturnType<typeof useListsByTemplate> }) {
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File>();
  const [target, setTarget] = useState('');
  const importFile = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const calendar = calendars.find((c) => c.id === (target || calendars[0]?.id))!;
      return listBuilder(calendar.workspaceId!, calendar.id!).calendar.importEscaped.post(await file!.arrayBuffer());
    },
    onSuccess: async (result) => {
      toast.success(`Imported: ${result?.created ?? 0} new, ${result?.updated ?? 0} updated.`);
      setFile(undefined);
      await queryClient.invalidateQueries({ queryKey: ['me', 'calendar'] });
    },
  });
  if (!calendars.length) return null;
  return (
    <>
      <FilePickerButton accept=".ics,text/calendar" multiple={false} onFiles={([f]) => setFile(f)}>
        <Upload /> Import .ics
      </FilePickerButton>
      <Dialog open={!!file} onOpenChange={(open) => !open && setFile(undefined)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Import {file?.name}</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3 px-5 pb-5">
            {importFile.isError && <p className="text-[13px] text-danger">{problemMessage(importFile.error)}</p>}
            <Label htmlFor="import-calendar">Into</Label>
            <Select
              id="import-calendar"
              value={target || calendars[0]?.id || ''}
              onChange={(e) => setTarget(e.target.value)}
            >
              {calendars.map((c) => (
                <option key={c.id} value={c.id!}>
                  {c.workspaceName} › {c.name}
                </option>
              ))}
            </Select>
          </div>
          <DialogFooter>
            <Button onClick={() => setFile(undefined)}>Cancel</Button>
            <Button variant="primary" disabled={importFile.isPending} onClick={() => importFile.mutate()}>
              Import
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
