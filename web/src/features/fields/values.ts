import type { FieldDefinitionDto } from '@paperdotnet/client';

export type FieldDefinition = FieldDefinitionDto;

/** A field value as a list of ids (people, lookups, terms), whether it is stored as one value or many. */
export function idsOf(value: unknown): string[] {
  if (value == null || value === '') return [];
  return (Array.isArray(value) ? value : [value]).filter((v): v is string => typeof v === 'string');
}

export function isEmpty(value: unknown): boolean {
  return value == null || value === '' || (Array.isArray(value) && value.length === 0);
}

/** The value to send for an edited field: null removes it (merge patch). */
export function normalize(field: FieldDefinition, value: unknown): unknown {
  if (isEmpty(value)) return null;
  if (field.type === 'number' || field.type === 'currency') {
    const number = typeof value === 'number' ? value : Number(value);
    return Number.isFinite(number) ? number : value;
  }
  return value;
}

/** Changed values only, for a merge patch (JSON compared, so arrays and objects work). */
export function changes(before: Record<string, unknown>, after: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [name, value] of Object.entries(after)) {
    const old = isEmpty(before[name]) ? null : before[name];
    const next = isEmpty(value) ? null : value;
    if (JSON.stringify(old) !== JSON.stringify(next)) result[name] = next;
  }
  return result;
}

/** A readable name for choice values stored as keys ("notStarted" → "Not started", "high" → "High"). */
export function choiceLabel(value: string): string {
  if (!/^[a-z][a-z0-9]*([A-Z][a-z0-9]+)*$/.test(value)) return value;
  const words = value.replace(/([A-Z])/g, ' $1').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
