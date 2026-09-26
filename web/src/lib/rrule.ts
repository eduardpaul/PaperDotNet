// A small, common subset of iCalendar recurrence rules (RFC 5545) for the repeat editors: frequency, interval,
// weekdays, and an end (date or count). The server parses and expands rules with Ical.Net; this only builds the text
// and reads it back. Rules it cannot represent are kept as they are and shown as custom.

export type Frequency = 'DAILY' | 'WEEKLY' | 'MONTHLY' | 'YEARLY';
export const Weekdays = ['MO', 'TU', 'WE', 'TH', 'FR', 'SA', 'SU'] as const;
export type Weekday = (typeof Weekdays)[number];

export interface Recurrence {
  frequency: Frequency;
  interval: number;
  weekdays: Weekday[];
  /** yyyy-MM-dd, inclusive. */
  until?: string;
  count?: number;
}

const Supported = new Set(['FREQ', 'INTERVAL', 'BYDAY', 'UNTIL', 'COUNT']);

export function formatRule(r: Recurrence): string {
  const parts = [`FREQ=${r.frequency}`];
  if (r.interval > 1) parts.push(`INTERVAL=${r.interval}`);
  if (r.frequency === 'WEEKLY' && r.weekdays.length)
    parts.push(`BYDAY=${Weekdays.filter((d) => r.weekdays.includes(d)).join(',')}`);
  if (r.until) parts.push(`UNTIL=${r.until.replaceAll('-', '')}T235959Z`);
  else if (r.count) parts.push(`COUNT=${r.count}`);
  return parts.join(';');
}

/** The rule as a Recurrence, or undefined when it uses parts these editors do not offer. */
export function parseRule(rule: string | null | undefined): Recurrence | undefined {
  if (!rule) return undefined;
  const values = new Map<string, string>();
  for (const part of rule.replace(/^RRULE:/i, '').split(';')) {
    const [key, value] = part.split('=');
    if (!key || value === undefined) return undefined;
    values.set(key.toUpperCase(), value.toUpperCase());
  }

  const frequency = values.get('FREQ') as Frequency | undefined;
  if (!frequency || !['DAILY', 'WEEKLY', 'MONTHLY', 'YEARLY'].includes(frequency)) return undefined;
  if ([...values.keys()].some((k) => !Supported.has(k))) return undefined;
  const weekdays = (values.get('BYDAY')?.split(',') ?? []) as Weekday[];
  if (weekdays.some((d) => !Weekdays.includes(d))) return undefined;
  const until = values.get('UNTIL');
  return {
    frequency,
    interval: Number(values.get('INTERVAL') ?? '1') || 1,
    weekdays,
    until: until ? `${until.slice(0, 4)}-${until.slice(4, 6)}-${until.slice(6, 8)}` : undefined,
    count: values.has('COUNT') ? Number(values.get('COUNT')) : undefined,
  };
}

const unit: Record<Frequency, [string, string]> = {
  DAILY: ['day', 'days'],
  WEEKLY: ['week', 'weeks'],
  MONTHLY: ['month', 'months'],
  YEARLY: ['year', 'years'],
};
const dayNames: Record<Weekday, string> = {
  MO: 'Mon',
  TU: 'Tue',
  WE: 'Wed',
  TH: 'Thu',
  FR: 'Fri',
  SA: 'Sat',
  SU: 'Sun',
};

/** "Every 2 weeks on Mon, Wed until 2026-12-31" */
export function describeRule(rule: string | null | undefined, formatDate: (date: string) => string = (d) => d): string {
  const r = parseRule(rule);
  if (!r) return rule ? `Custom: ${rule}` : 'Does not repeat';
  const [one, many] = unit[r.frequency];
  let text = r.interval === 1 ? `Every ${one}` : `Every ${r.interval} ${many}`;
  if (r.frequency === 'WEEKLY' && r.weekdays.length) text += ` on ${r.weekdays.map((d) => dayNames[d]).join(', ')}`;
  if (r.until) text += ` until ${formatDate(r.until)}`;
  else if (r.count) text += `, ${r.count} times`;
  return text;
}
