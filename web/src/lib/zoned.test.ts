import { describe, expect, it } from 'vitest';
import { fromZonedInput, toZonedInput } from './zoned';

describe('zoned date-time inputs', () => {
  it('shows instants as wall-clock time in the zone', () => {
    expect(toZonedInput('2026-07-01T10:00:00Z', 'Europe/Berlin')).toBe('2026-07-01T12:00');
    expect(toZonedInput('2026-07-01T10:00:00Z', 'America/Los_Angeles')).toBe('2026-07-01T03:00');
    expect(toZonedInput(null, 'UTC')).toBe('');
  });

  it('reads wall-clock time in the zone back as UTC', () => {
    expect(fromZonedInput('2026-07-01T12:00', 'Europe/Berlin')).toBe('2026-07-01T10:00:00.000Z');
    expect(fromZonedInput('2026-01-15T09:30', 'America/New_York')).toBe('2026-01-15T14:30:00.000Z');
    expect(fromZonedInput('not a date', 'UTC')).toBeNull();
  });

  it('round-trips across daylight saving changes', () => {
    for (const text of ['2026-03-29T03:30', '2026-10-25T01:00', '2026-10-25T04:00']) {
      expect(toZonedInput(fromZonedInput(text, 'Europe/Berlin'), 'Europe/Berlin')).toBe(text);
    }
  });

  it('moves a skipped time forward and takes the earlier of a repeated one', () => {
    // 02:30 does not exist in Berlin on 2026-03-29 (clocks jump from 02:00 to 03:00).
    expect(toZonedInput(fromZonedInput('2026-03-29T02:30', 'Europe/Berlin'), 'Europe/Berlin')).toBe('2026-03-29T03:30');
    // 02:30 happens twice on 2026-10-25; the first is still summer time (UTC+2).
    expect(fromZonedInput('2026-10-25T02:30', 'Europe/Berlin')).toBe('2026-10-25T00:30:00.000Z');
  });
});
