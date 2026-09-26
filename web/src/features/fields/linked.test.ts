import { describe, expect, it } from 'vitest';
import { withLinkedValues } from './linked';

describe('linked values', () => {
  it('moves an event end with its start', () => {
    const next = withLinkedValues(
      { start: '2026-09-26T09:00:00.000Z', end: '2026-09-26T10:00:00.000Z' },
      'start',
      '2026-09-26T14:00:00.000Z',
    );
    expect(next.end).toBe('2026-09-26T15:00:00.000Z');
  });

  it('leaves other fields alone', () => {
    expect(withLinkedValues({ start: 'x', end: 'y' }, 'title', 'z')).toEqual({ start: 'x', end: 'y', title: 'z' });
    expect(withLinkedValues({ start: '2026-01-01T00:00:00Z' }, 'start', '2026-01-02T00:00:00Z')).toEqual({
      start: '2026-01-02T00:00:00Z',
    });
  });
});
