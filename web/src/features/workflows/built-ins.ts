// Parameters of built-in workflows (EVT-12): a small form from their JSON Schema (string, number, boolean and
// arrays of strings, which are typed as a comma-separated list).

export interface ParameterField {
  name: string;
  type: string;
  description?: string;
  required: boolean;
  defaultValue?: unknown;
}

/** The form's value of a field: text, or a flag for booleans. */
export type ParameterInput = string | boolean;

/** The fields of a parameter schema, required ones first. */
export function parameterFields(schema: Record<string, unknown> | undefined): ParameterField[] {
  const properties = (schema?.properties ?? {}) as Record<string, Record<string, unknown>>;
  const required = new Set(Array.isArray(schema?.required) ? (schema.required as string[]) : []);
  return Object.entries(properties)
    .map(([name, property]) => ({
      name,
      type: typeof property.type === 'string' ? property.type : 'string',
      description: typeof property.description === 'string' ? property.description : undefined,
      required: required.has(name),
      defaultValue: property.default,
    }))
    .sort((a, b) => Number(b.required) - Number(a.required));
}

/** The form's starting values: the saved values, else the defaults. */
export function parameterInputs(
  fields: ParameterField[],
  values: Record<string, unknown> | undefined,
): Record<string, ParameterInput> {
  return Object.fromEntries(
    fields.map((field) => {
      const value = values?.[field.name] ?? field.defaultValue;
      if (field.type === 'boolean') return [field.name, value === true];
      if (Array.isArray(value)) return [field.name, value.join(', ')];
      return [field.name, value === undefined || value === null ? '' : String(value)];
    }),
  );
}

/** The values to save: typed by the schema; empty fields are left out (the server applies the defaults). */
export function parameterValues(
  fields: ParameterField[],
  inputs: Record<string, ParameterInput>,
): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  for (const field of fields) {
    const input = inputs[field.name];
    if (field.type === 'boolean') {
      values[field.name] = input === true;
      continue;
    }

    const text = typeof input === 'string' ? input.trim() : '';
    if (!text) continue;
    if (field.type === 'array') {
      values[field.name] = text
        .split(',')
        .map((part) => part.trim())
        .filter(Boolean);
    } else if (field.type === 'number' || field.type === 'integer') {
      values[field.name] = Number(text);
    } else {
      values[field.name] = text;
    }
  }

  return values;
}
