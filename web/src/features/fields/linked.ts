/**
 * Values that move together: when an event's start moves, its end moves with it and keeps the duration (a new
 * start after the end would otherwise be refused).
 */
export function withLinkedValues(
  current: Record<string, unknown>,
  name: string,
  value: unknown,
): Record<string, unknown> {
  const next = { ...current, [name]: value };
  if (
    name === 'start' &&
    typeof current.start === 'string' &&
    typeof current.end === 'string' &&
    typeof value === 'string'
  ) {
    const moved = Date.parse(value) - Date.parse(current.start);
    const end = Date.parse(current.end) + moved;
    if (Number.isFinite(end)) next.end = new Date(end).toISOString();
  }
  return next;
}
