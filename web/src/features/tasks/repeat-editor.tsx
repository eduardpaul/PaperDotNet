import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input, Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import {
  describeRule,
  formatRule,
  parseRule,
  Weekdays,
  type Frequency,
  type Recurrence,
  type Weekday,
} from '@/lib/rrule';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

const dayLetters: Record<Weekday, string> = { MO: 'M', TU: 'T', WE: 'W', TH: 'T', FR: 'F', SA: 'S', SU: 'S' };
const dayNames: Record<Weekday, string> = {
  MO: 'Monday',
  TU: 'Tuesday',
  WE: 'Wednesday',
  TH: 'Thursday',
  FR: 'Friday',
  SA: 'Saturday',
  SU: 'Sunday',
};

/** Edits a repeat rule (TSK-05, CAL-02): frequency, interval, weekdays and end. */
export function RepeatEditor({
  rule,
  busy,
  onSave,
  onRemove,
}: {
  rule: string | undefined;
  busy?: boolean;
  onSave: (rule: string) => void;
  onRemove?: () => void;
}) {
  const format = useFormat();
  const parsed = parseRule(rule);
  const [value, setValue] = useState<Recurrence>(parsed ?? { frequency: 'WEEKLY', interval: 1, weekdays: [] });
  const [end, setEnd] = useState<'never' | 'until' | 'count'>(
    parsed?.until ? 'until' : parsed?.count ? 'count' : 'never',
  );
  const next = formatRule({
    ...value,
    until: end === 'until' ? value.until : undefined,
    count: end === 'count' ? value.count : undefined,
  });
  const set = (patch: Partial<Recurrence>) => setValue((current) => ({ ...current, ...patch }));

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[13px]">
        <span className="text-muted">Now: </span>
        {describeRule(rule, (d) => format.date(d))}
      </p>
      {rule && !parsed && <p className="text-xs text-muted">This rule was made elsewhere; saving replaces it.</p>}
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="repeat-interval">Every</Label>
          <Input
            id="repeat-interval"
            type="number"
            min={1}
            max={99}
            className="w-20"
            value={value.interval}
            onChange={(e) => set({ interval: Math.max(1, e.target.valueAsNumber || 1) })}
          />
        </div>
        <Select
          aria-label="Frequency"
          className="w-32"
          value={value.frequency}
          onChange={(e) => set({ frequency: e.target.value as Frequency })}
        >
          <option value="DAILY">{value.interval > 1 ? 'days' : 'day'}</option>
          <option value="WEEKLY">{value.interval > 1 ? 'weeks' : 'week'}</option>
          <option value="MONTHLY">{value.interval > 1 ? 'months' : 'month'}</option>
          <option value="YEARLY">{value.interval > 1 ? 'years' : 'year'}</option>
        </Select>
      </div>
      {value.frequency === 'WEEKLY' && (
        <div role="group" aria-label="On" className="flex gap-1">
          {Weekdays.map((day) => {
            const on = value.weekdays.includes(day);
            return (
              <button
                key={day}
                type="button"
                aria-pressed={on}
                aria-label={dayNames[day]}
                onClick={() =>
                  set({ weekdays: on ? value.weekdays.filter((d) => d !== day) : [...value.weekdays, day] })
                }
                className={cn(
                  'size-8 rounded-full border text-xs font-medium',
                  on ? 'border-accent bg-accent text-accent-foreground' : 'hover:bg-surface-muted',
                )}
              >
                {dayLetters[day]}
              </button>
            );
          })}
        </div>
      )}
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="repeat-end">Ends</Label>
          <Select id="repeat-end" className="w-32" value={end} onChange={(e) => setEnd(e.target.value as typeof end)}>
            <option value="never">Never</option>
            <option value="until">On</option>
            <option value="count">After</option>
          </Select>
        </div>
        {end === 'until' && (
          <Input
            aria-label="End date"
            type="date"
            className="w-44"
            value={value.until ?? ''}
            onChange={(e) => set({ until: e.target.value })}
          />
        )}
        {end === 'count' && (
          <span className="flex items-center gap-2 text-[13px]">
            <Input
              aria-label="Occurrences"
              type="number"
              min={1}
              className="w-20"
              value={value.count ?? 10}
              onChange={(e) => set({ count: Math.max(1, e.target.valueAsNumber || 1) })}
            />
            times
          </span>
        )}
      </div>
      <p className="text-xs text-muted">{describeRule(next, (d) => format.date(d))}</p>
      <div className="flex gap-2">
        <Button variant="primary" size="sm" disabled={busy || next === rule} onClick={() => onSave(next)}>
          Save repeat
        </Button>
        {rule && onRemove && (
          <Button size="sm" disabled={busy} onClick={onRemove}>
            Stop repeating
          </Button>
        )}
      </div>
    </div>
  );
}
