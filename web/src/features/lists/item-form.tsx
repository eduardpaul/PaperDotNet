import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import { fields as fieldValues, fieldsOf, ifMatch, isStatus, jsonOf, validationErrors } from '@paperdotnet/client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { RotateCcw, Save } from 'lucide-react';
import { useMemo, useState, type FormEvent } from 'react';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Alert, Spinner } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { FieldEditor } from '@/features/fields/editors';
import { ValueNamesProvider } from '@/features/fields/lookups';
import { changes, normalize } from '@/features/fields/values';
import { problemMessage } from '@/lib/errors';
import { listBuilder } from './queries';
import { contentTypeOf, fieldLabel, withTitle } from './schema';

/**
 * Creates or edits an item. Only changed values are sent (merge patch, null clears a value) with If-Match, so a
 * change by someone else is never overwritten silently: the user reloads or explicitly saves over it.
 */
export function ItemForm({
  workspaceId,
  list,
  item,
  parentId,
  onSaved,
  onCancel,
}: {
  workspaceId: string;
  list: ListResponse;
  item?: ItemResponse;
  parentId?: string;
  onSaved: (item: ItemResponse) => void;
  onCancel?: () => void;
}) {
  const queryClient = useQueryClient();
  const [contentTypeId, setContentTypeId] = useState(item?.contentTypeId ?? list.contentTypes?.[0]?.id ?? undefined);
  const contentType = contentTypeOf(list, contentTypeId);
  const fields = useMemo(() => withTitle(contentType?.fields ?? list.columns), [contentType, list.columns]);
  const original = useMemo(() => (item ? fieldsOf(item) : {}), [item]);
  const [values, setValues] = useState<Record<string, unknown>>(() => ({ ...defaults(fields, !item), ...original }));
  const [conflict, setConflict] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const items = listBuilder(workspaceId, list.id!).items;
  const edited = item ? changes(original, values) : values;
  const dirty = !item || Object.keys(edited).length > 0;

  const save = useMutation({
    meta: { silent: true },
    mutationFn: async ({ force }: { force?: boolean }) => {
      const body = Object.fromEntries(
        Object.entries(edited).map(([name, value]) => [
          name,
          normalize(fields.find((f) => f.name === name) ?? { name, type: 'text' }, value),
        ]),
      );
      if (!item) {
        return (await items.post({ contentTypeId, parentId, fields: fieldValues(body) }))!;
      }
      // "Save anyway" sends the same changes over the latest version.
      const current = force ? await items.byItemId(item.id!).get() : item;
      return (await items.byItemId(item.id!).patch({ fields: fieldValues(body) }, ifMatch(current)))!;
    },
    onMutate: () => {
      setErrors({});
      setConflict(false);
    },
    onSuccess: async (saved) => {
      queryClient.setQueryData(keys.item(workspaceId, list.id!, saved.id!), saved);
      await queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, list.id!) });
      onSaved(saved);
    },
    onError: (error) => {
      if (isStatus(error, 412)) setConflict(true);
      setErrors(validationErrors(error));
    },
  });

  const reload = async () => {
    const latest = await queryClient.fetchQuery({
      queryKey: keys.item(workspaceId, list.id!, item!.id!),
      staleTime: 0,
      queryFn: async () => (await items.byItemId(item!.id!).get())!,
    });
    setValues(fieldsOf(latest));
    setConflict(false);
    onSaved(latest);
  };

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate({});
  };

  const general = Object.entries(errors).filter(([key]) => !key.startsWith('fields.'));

  return (
    <ValueNamesProvider fields={fields} values={[values]}>
      <form onSubmit={onSubmit} className="flex min-h-0 flex-1 flex-col">
        <div className="flex-1 space-y-4 overflow-y-auto px-5 py-4">
          {conflict && (
            <Alert tone="warning" className="flex flex-col gap-2">
              <span>Someone else changed this item after you opened it.</span>
              <span className="flex gap-2">
                <Button size="sm" onClick={() => void reload()}>
                  <RotateCcw /> Reload their version
                </Button>
                <Button size="sm" variant="danger" onClick={() => save.mutate({ force: true })}>
                  Save mine anyway
                </Button>
              </span>
            </Alert>
          )}
          {save.isError && !conflict && Object.keys(errors).length === 0 && <Alert>{problemMessage(save.error)}</Alert>}
          {general.map(([key, messages]) => (
            <Alert key={key}>{messages.join(' ')}</Alert>
          ))}
          {!item && (list.contentTypes?.length ?? 0) > 1 && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="content-type">Type</Label>
              <Select id="content-type" value={contentTypeId} onChange={(e) => setContentTypeId(e.target.value)}>
                {list.contentTypes!.map((c) => (
                  <option key={c.id} value={c.id!}>
                    {c.name}
                  </option>
                ))}
              </Select>
            </div>
          )}
          {fields.map((field) => {
            const id = `field-${field.name}`;
            const messages = errors[`fields.${field.name}`];
            return (
              <div key={field.name} className="flex flex-col gap-1.5">
                {field.type !== 'boolean' && (
                  <Label id={`${id}-label`} htmlFor={id}>
                    {fieldLabel(field)}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                  </Label>
                )}
                <FieldEditor
                  id={id}
                  field={field}
                  value={values[field.name!]}
                  invalid={!!messages}
                  onChange={(value) => setValues((current) => ({ ...current, [field.name!]: value }))}
                />
                {field.description && !messages && <p className="text-xs text-muted">{field.description}</p>}
                {messages && <p className="text-xs text-danger">{messages.join(' ')}</p>}
              </div>
            );
          })}
        </div>
        <div className="flex items-center justify-end gap-2 border-t bg-surface px-5 py-3">
          {item && dirty && <span className="mr-auto text-xs text-muted">Unsaved changes</span>}
          {onCancel && <Button onClick={onCancel}>{item ? 'Close' : 'Cancel'}</Button>}
          {item && dirty && (
            <Button onClick={() => setValues({ ...original })} disabled={save.isPending}>
              Discard
            </Button>
          )}
          <Button type="submit" variant="primary" disabled={!dirty || save.isPending}>
            {save.isPending ? <Spinner className="text-current" /> : <Save />}
            {item ? 'Save' : 'Create'}
          </Button>
        </div>
      </form>
    </ValueNamesProvider>
  );
}

function defaults(fields: ReturnType<typeof withTitle>, isNew: boolean): Record<string, unknown> {
  if (!isNew) return {};
  const values: Record<string, unknown> = {};
  for (const field of fields) {
    const value = jsonOf(field.defaultValue);
    if (value !== undefined && value !== null) values[field.name!] = value;
  }
  return values;
}
