import type { RJSFSchema, UiSchema } from '@rjsf/utils';

/** Use the same domain picker in launch and approval forms, including nested objects and arrays. */
export function domainUiSchema(schema: RJSFSchema): UiSchema {
  if (schema['x-paperdotnet']) {
    return {
      'ui:field':
        (schema['x-paperdotnet'] as { kind?: string }).kind === 'people' ? 'PeopleSelection' : 'DomainSelection',
    };
  }
  const ui: UiSchema = {};
  for (const [name, child] of Object.entries(schema.properties ?? {})) {
    if (typeof child === 'object') ui[name] = domainUiSchema(child);
  }
  if (schema.items && !Array.isArray(schema.items) && typeof schema.items === 'object') {
    ui.items = domainUiSchema(schema.items);
  }
  return ui;
}

/** Required domain selections need at least one ID. */
export function domainSchema(schema: RJSFSchema): RJSFSchema {
  const result = { ...schema };
  if (schema.properties) {
    result.properties = Object.fromEntries(
      Object.entries(schema.properties).map(([name, child]) => {
        if (typeof child !== 'object') return [name, child];
        const normalized = domainSchema(child);
        if ((child as RJSFSchema)['x-paperdotnet'] && schema.required?.includes(name)) {
          if (child.type === 'array') normalized.minItems = Math.max(1, child.minItems ?? 0);
          if (child.type === 'string') normalized.minLength = Math.max(1, child.minLength ?? 0);
        }
        return [name, normalized];
      }),
    );
  }
  if (schema.items && !Array.isArray(schema.items) && typeof schema.items === 'object')
    result.items = domainSchema(schema.items);
  return result;
}
