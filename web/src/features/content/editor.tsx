import type { ContentField, ContentSchema, ContentSnapshot, ContentValues } from '@paperdotnet/client';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { DialogFooter } from '@/components/ui/dialog';
import { Input, Label, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';

export function ContentEditor({
  schema,
  item,
  folder,
  busy,
  onSave,
}: {
  schema: ContentSchema;
  item?: ContentSnapshot;
  folder: boolean;
  busy: boolean;
  onSave: (content: ContentValues) => Promise<void>;
}) {
  const [name, setName] = useState(item?.resource.name ?? '');
  const [typeId, setType] = useState(
    item?.contentTypeId ?? schema.content_types.find((t) => t.is_default)?.id ?? schema.content_types[0]?.id ?? '',
  );
  const [values, setValues] = useState<Record<string, unknown>>(
    () =>
      item?.values ??
      Object.fromEntries(
        schema.fields
          .filter((f) => f.options?.default_value !== undefined)
          .map((f) => [f.id, f.options!.default_value]),
      ),
  );
  const type = schema.content_types.find((t) => t.id === typeId);
  const fields = folder ? [] : schema.fields.filter((f) => type?.field_keys.includes(f.id));
  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        void onSave({
          name,
          contentTypeId: typeId,
          values: Object.fromEntries(
            fields.filter((f) => values[f.id] !== undefined && values[f.id] !== '').map((f) => [f.id, values[f.id]]),
          ),
          keywords: item?.keywords ?? [],
        });
      }}
    >
      <div className="space-y-4 px-5 pb-5">
        <div>
          <Label htmlFor="content-item-name">Name</Label>
          <Input id="content-item-name" required value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        {!folder && schema.content_types.length > 1 && (
          <div>
            <Label htmlFor="content-item-type">Content type</Label>
            <Select id="content-item-type" value={typeId} onChange={(e) => setType(e.target.value)}>
              {schema.content_types.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </Select>
          </div>
        )}
        {fields.map((field) => (
          <div key={field.id}>
            <Label htmlFor={`content-field-${field.id}`}>
              {field.label}
              {field.required ? ' *' : ''}
            </Label>
            <FieldInput
              field={field}
              value={values[field.id]}
              onChange={(value) => setValues((c) => ({ ...c, [field.id]: value }))}
            />
          </div>
        ))}
      </div>
      <DialogFooter>
        <Button variant="primary" type="submit" disabled={busy || !typeId}>
          Save{item ? ' changes' : ''}
        </Button>
      </DialogFooter>
    </form>
  );
}
function FieldInput({
  field,
  value,
  onChange,
}: {
  field: ContentField;
  value: unknown;
  onChange: (value: unknown) => void;
}) {
  const id = `content-field-${field.id}`;
  if (field.options?.multiple)
    return (
      <Textarea
        id={id}
        required={field.required}
        placeholder="One value per line"
        value={Array.isArray(value) ? value.map(String).join('\n') : ''}
        onChange={(e) => onChange(e.target.value.split('\n').filter(Boolean))}
      />
    );
  if (field.type === 'boolean')
    return (
      <div>
        <Checkbox id={id} checked={value === true} onChange={(e) => onChange(e.target.checked)} />
      </div>
    );
  if (field.type === 'choice')
    return (
      <Select id={id} required={field.required} value={String(value ?? '')} onChange={(e) => onChange(e.target.value)}>
        <option value="">Choose…</option>
        {field.choices?.map((c) => (
          <option key={c}>{c}</option>
        ))}
      </Select>
    );
  if (field.type === 'note')
    return (
      <Textarea
        id={id}
        required={field.required}
        value={String(value ?? '')}
        onChange={(e) => onChange(e.target.value)}
      />
    );
  return (
    <Input
      id={id}
      required={field.required}
      maxLength={field.options?.max_length}
      type={['date', 'email', 'url'].includes(field.type) ? field.type : 'text'}
      inputMode={['integer', 'decimal', 'number', 'currency'].includes(field.type) ? 'decimal' : undefined}
      placeholder={field.type === 'datetime' ? '2026-10-24T12:00:00Z' : undefined}
      value={String(value ?? '')}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}
