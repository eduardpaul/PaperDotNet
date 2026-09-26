// Date-time inputs in the user's time zone (PLT-17), not the browser's: <input type="datetime-local"> has no zone,
// so values are converted between UTC instants and wall-clock text ("yyyy-MM-ddTHH:mm") in a given IANA zone.

function wallClock(instant: Date, timeZone: string): { y: number; m: number; d: number; h: number; min: number } {
  const parts: Record<string, string> = {};
  const format = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  });
  for (const part of format.formatToParts(instant)) parts[part.type] = part.value;
  return { y: +parts.year!, m: +parts.month!, d: +parts.day!, h: +parts.hour!, min: +parts.minute! };
}

/** Milliseconds the zone is ahead of UTC at this instant. */
function offset(instant: Date, timeZone: string): number {
  const w = wallClock(instant, timeZone);
  const asUtc = Date.UTC(w.y, w.m - 1, w.d, w.h, w.min);
  return asUtc - Math.floor(instant.getTime() / 60_000) * 60_000;
}

const pad = (n: number) => String(n).padStart(2, '0');

/** An instant as datetime-local text in the zone: 2026-03-29T01:30. */
export function toZonedInput(value: string | Date | null | undefined, timeZone: string): string {
  if (value == null || value === '') return '';
  const instant = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(instant.getTime())) return '';
  const w = wallClock(instant, timeZone);
  return `${w.y}-${pad(w.m)}-${pad(w.d)}T${pad(w.h)}:${pad(w.min)}`;
}

/**
 * datetime-local text in the zone as a UTC ISO instant. A time that does not exist (skipped by a DST change) moves
 * forward by the gap; an ambiguous one (repeated) takes the earlier instant.
 */
export function fromZonedInput(text: string, timeZone: string): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(text);
  if (!match) return null;
  const [, y, m, d, h, min] = match.map(Number) as [number, number, number, number, number, number];
  const wall = Date.UTC(y, m - 1, d, h, min);
  // The zone's offsets a day before and after cover any DST change around this wall-clock time.
  const before = offset(new Date(wall - 86_400_000), timeZone);
  const after = offset(new Date(wall + 86_400_000), timeZone);
  const matches = [wall - before, wall - after].filter(
    (t) => toZonedInput(new Date(t), timeZone) === text.slice(0, 16),
  );
  // Repeated time: the earlier instant. Skipped time: shifted forward by the gap (the offset before the change).
  const instant = matches.length > 0 ? Math.min(...matches) : wall - before;
  return new Date(instant).toISOString();
}
