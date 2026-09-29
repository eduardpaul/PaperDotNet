import type { ItemResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import {
  addMonths,
  eachDayOfInterval,
  endOfMonth,
  endOfWeek,
  format as formatDate,
  parseISO,
  startOfMonth,
  startOfWeek,
} from 'date-fns';
import { CalendarDays, ChevronLeft, ChevronRight } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState } from '@/components/ui/feedback';
import { DocumentGrid } from '@/features/documents/document-grid';
import { FieldValue } from '@/features/fields/display';
import type { FieldDefinition } from '@/features/fields/values';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';
import { cn } from '@/lib/utils';

/** Gallery of a library (thumbnails) or of any other list (title cards). */
export function ItemsGallery({
  workspaceId,
  listId,
  items,
  isLibrary,
  fields,
  onOpen,
}: {
  workspaceId: string;
  listId: string;
  items: ItemResponse[];
  isLibrary: boolean;
  fields: FieldDefinition[];
  onOpen: (item: ItemResponse) => void;
}) {
  if (isLibrary) return <DocumentGrid workspaceId={workspaceId} listId={listId} items={items} onOpen={onOpen} />;
  const shown = fields.filter((f) => f.name !== 'title').slice(0, 3);
  return (
    <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
      {items.map((item) => {
        const values = fieldsOf(item);
        return (
          <li key={item.id}>
            <button
              type="button"
              onClick={() => onOpen(item)}
              className="flex w-full flex-col gap-2 rounded-lg border bg-surface p-3 text-left shadow-xs hover:border-accent/50"
            >
              <span className="truncate text-[13px] font-medium">{String(values.title ?? 'Untitled')}</span>
              {shown.map((field) => (
                <span key={field.name} className="truncate text-xs text-muted">
                  <FieldValue field={field} value={values[field.name!]} />
                </span>
              ))}
            </button>
          </li>
        );
      })}
    </ul>
  );
}

function dayOf(
  item: ItemResponse,
  field: FieldDefinition,
  dayKey: (value: Date | string) => string,
): string | undefined {
  const value = fieldsOf(item)[field.name!];
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}/.test(value)) return undefined;
  return field.type === 'date' ? value.slice(0, 10) : dayKey(value);
}

/** A month of items grouped by a date field (LST-09 calendar layout). */
export function ItemsCalendar({
  items,
  dateField,
  onOpen,
}: {
  items: ItemResponse[];
  dateField: FieldDefinition | undefined;
  onOpen: (item: ItemResponse) => void;
}) {
  const format = useFormat();
  const today = format.dayKey(useNow());
  const [cursor, setCursor] = useState(today.slice(0, 7));
  const anchor = parseISO(`${cursor}-01`);
  const first = startOfWeek(startOfMonth(anchor), { weekStartsOn: 1 });
  const last = endOfWeek(endOfMonth(anchor), { weekStartsOn: 1 });
  const days = eachDayOfInterval({ start: first, end: last }).map((d) => formatDate(d, 'yyyy-MM-dd'));
  const byDay = new Map<string, ItemResponse[]>();
  const undated: ItemResponse[] = [];
  for (const item of items) {
    const day = dateField ? dayOf(item, dateField, format.dayKey) : undefined;
    if (!day) undated.push(item);
    else byDay.set(day, [...(byDay.get(day) ?? []), item]);
  }

  if (!dateField) {
    return (
      <Card>
        <EmptyState icon={CalendarDays} title="This calendar needs a date column">
          Choose a date field for the view in the list settings.
        </EmptyState>
      </Card>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <Button
          size="icon"
          aria-label="Previous month"
          onClick={() => setCursor(formatDate(addMonths(anchor, -1), 'yyyy-MM'))}
        >
          <ChevronLeft />
        </Button>
        <span className="min-w-36 text-sm font-semibold">{format.monthYear(anchor)}</span>
        <Button
          size="icon"
          aria-label="Next month"
          onClick={() => setCursor(formatDate(addMonths(anchor, 1), 'yyyy-MM'))}
        >
          <ChevronRight />
        </Button>
      </div>
      <div className="overflow-hidden rounded-lg border bg-surface">
        <div className="grid grid-cols-7 border-b text-center text-xs font-medium text-muted">
          {days.slice(0, 7).map((d) => (
            <div key={d} className="py-2">
              {format.weekday(d, 'short')}
            </div>
          ))}
        </div>
        <div className="grid grid-cols-7">
          {days.map((day) => {
            const rows = byDay.get(day) ?? [];
            const inMonth = day.slice(0, 7) === cursor;
            return (
              <div
                key={day}
                className={cn(
                  'min-h-24 border-r border-b p-1.5 [&:nth-child(7n)]:border-r-0',
                  !inMonth && 'bg-surface-muted/40',
                )}
              >
                <span
                  className={cn(
                    'mb-1 flex size-6 items-center justify-center rounded-full text-xs',
                    day === today && 'bg-accent font-semibold text-accent-foreground',
                    !inMonth && day !== today && 'text-muted',
                  )}
                >
                  {Number(day.slice(8))}
                </span>
                <ul className="flex flex-col gap-0.5">
                  {rows.slice(0, 3).map((item) => (
                    <li key={item.id}>
                      <button
                        type="button"
                        onClick={() => onOpen(item)}
                        className="w-full truncate rounded px-1 text-left text-[11px] hover:bg-accent-soft"
                      >
                        {String(fieldsOf(item).title ?? 'Untitled')}
                      </button>
                    </li>
                  ))}
                  {rows.length > 3 && <li className="px-1 text-[11px] text-muted">+{rows.length - 3}</li>}
                </ul>
              </div>
            );
          })}
        </div>
      </div>
      {undated.length > 0 && (
        <div>
          <h3 className="mb-1 text-xs font-medium text-muted">No {dateField.displayName || dateField.name}</h3>
          <ul className="flex flex-col gap-1">
            {undated.map((item) => (
              <li key={item.id}>
                <button type="button" className="text-[13px] hover:underline" onClick={() => onOpen(item)}>
                  {String(fieldsOf(item).title ?? 'Untitled')}
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
