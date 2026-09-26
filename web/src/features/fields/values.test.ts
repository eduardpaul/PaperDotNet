import { describe, expect, it } from 'vitest';
import { changes, choiceLabel, idsOf, normalize } from './values';

describe('field values', () => {
  it('reads ids from single and multiple values', () => {
    expect(idsOf('a')).toEqual(['a']);
    expect(idsOf(['a', 'b'])).toEqual(['a', 'b']);
    expect(idsOf(null)).toEqual([]);
  });

  it('keeps only changed values and removes cleared ones', () => {
    expect(changes({ title: 'A', tags: ['x'], note: 'n' }, { title: 'A', tags: ['x', 'y'], note: '' })).toEqual({
      tags: ['x', 'y'],
      note: null,
    });
    expect(changes({ amount: 5 }, { amount: 5, extra: '' })).toEqual({});
  });

  it('sends numbers as numbers', () => {
    expect(normalize({ name: 'n', type: 'number' }, '12.5')).toBe(12.5);
    expect(normalize({ name: 'n', type: 'text' }, '')).toBeNull();
  });

  it('shows camelCase choices as words', () => {
    expect(choiceLabel('notStarted')).toBe('Not started');
    expect(choiceLabel('high')).toBe('High');
    expect(choiceLabel('iOS')).toBe('iOS');
    expect(choiceLabel('In review')).toBe('In review');
  });
});
