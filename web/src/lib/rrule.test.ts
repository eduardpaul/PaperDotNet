import { describe, expect, it } from 'vitest';
import { describeRule, formatRule, parseRule } from './rrule';

describe('recurrence rules', () => {
  it('formats and reads back the supported parts', () => {
    const rule = formatRule({ frequency: 'WEEKLY', interval: 2, weekdays: ['WE', 'MO'], until: '2026-12-31' });
    expect(rule).toBe('FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20261231T235959Z');
    expect(parseRule(rule)).toEqual({
      frequency: 'WEEKLY',
      interval: 2,
      weekdays: ['MO', 'WE'],
      until: '2026-12-31',
      count: undefined,
    });
    expect(formatRule({ frequency: 'MONTHLY', interval: 1, weekdays: [], count: 6 })).toBe('FREQ=MONTHLY;COUNT=6');
  });

  it('leaves rules with other parts as custom', () => {
    expect(parseRule('FREQ=MONTHLY;BYMONTHDAY=-1')).toBeUndefined();
    expect(parseRule('FREQ=HOURLY')).toBeUndefined();
    expect(describeRule('FREQ=MONTHLY;BYMONTHDAY=-1')).toBe('Custom: FREQ=MONTHLY;BYMONTHDAY=-1');
  });

  it('describes rules in words', () => {
    expect(describeRule('FREQ=DAILY')).toBe('Every day');
    expect(describeRule('FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,FR')).toBe('Every 2 weeks on Mon, Fri');
    expect(describeRule('FREQ=YEARLY;COUNT=3')).toBe('Every year, 3 times');
    expect(describeRule(undefined)).toBe('Does not repeat');
  });
});
