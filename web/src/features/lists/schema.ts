import type { ContentTypeResponse, ListResponse } from '@paperdotnet/client';
import type { FieldDefinition } from '@/features/fields/values';

const titleField: FieldDefinition = {
  name: 'title',
  displayName: 'Title',
  type: 'text',
  required: true,
  maxLength: 255,
};

/** Title first, then the other fields (every item has a title, whether the content type lists it or not). */
export function withTitle(fields: FieldDefinition[] | null | undefined): FieldDefinition[] {
  const rest = (fields ?? []).filter((f) => f.name !== 'title');
  const title = (fields ?? []).find((f) => f.name === 'title') ?? titleField;
  return [title, ...rest];
}

/** The content type an item uses, or the list's first one. */
export function contentTypeOf(
  list: ListResponse | undefined,
  contentTypeId: string | null | undefined,
): ContentTypeResponse | undefined {
  return list?.contentTypes?.find((c) => c.id === contentTypeId) ?? list?.contentTypes?.[0];
}

/** Every field of the list (all content types), by name, for columns and filters. */
export function listFields(list: ListResponse | undefined): FieldDefinition[] {
  const byName = new Map<string, FieldDefinition>();
  for (const field of [...(list?.columns ?? []), ...(list?.contentTypes ?? []).flatMap((c) => c.fields ?? [])]) {
    if (field.name && !byName.has(field.name)) byName.set(field.name, field);
  }
  return withTitle([...byName.values()]);
}

export function fieldLabel(field: FieldDefinition): string {
  return field.displayName || field.name || '';
}
