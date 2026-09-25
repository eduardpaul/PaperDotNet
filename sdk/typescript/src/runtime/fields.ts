// Item values are a JSON object per content type (JsonObject: its properties are in `additionalData`).
import type { UntypedNode } from '@microsoft/kiota-abstractions';
import type { JsonObject } from '../generated/models/index.js';

/** The item's values as a plain object: `fieldsOf(item).title`. */
export function fieldsOf(holder: { fields?: JsonObject | null } | undefined | null): Record<string, unknown> {
  return (holder?.fields?.additionalData ?? {}) as Record<string, unknown>;
}

/** Values for create and update bodies: `{ fields: fields({ title: 'Invoice', amount: 120 }) }`; null removes a value. */
export function fields(values: Record<string, unknown>): JsonObject {
  return { additionalData: { ...values } };
}

/** A JSON value of any shape (operation results, run logs) as plain data: `jsonOf(operation.result)`. */
export function jsonOf(node: UntypedNode | null | undefined): unknown {
  if (node === null || node === undefined) {
    return undefined;
  }

  const value = node.getValue();
  if (Array.isArray(value)) {
    return value.map((entry) => jsonOf(entry as UntypedNode));
  }

  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value as Record<string, UntypedNode>).map(([key, entry]) => [key, jsonOf(entry)]));
  }

  return value;
}
