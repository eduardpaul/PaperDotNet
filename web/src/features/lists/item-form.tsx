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
import { FieldValue } from '@/features/fields/display';
import { FieldEditor } from '@/features/fields/editors';
import { NoteEditor } from '@/features/notes/note-editor';
import { ValueNamesProvider } from '@/features/fields/lookups';
import { withLinkedValues } from '@/features/fields/linked';
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
  initialValues,
  readOnly,
  onSaved,
  onCancel,
}: {
  workspaceId: string;
  list: ListResponse;
  item?: ItemResponse;
  parentId?: string;
  /** Values a new item starts with (e.g. the day clicked in the calendar). */
  initialValues?: Record<string, unknown>;
  /** A reader sees the values and cannot save. */
  readOnly?: boolean;
  onSaved: (item: ItemResponse) => void;
  onCancel?: () => void;
}) {
  const queryClient = useQueryClient();
  const [contentTypeId, setContentTypeId] = useState(item?.contentTypeId ?? list.contentTypes?.[0]?.id ?? undefined);
  const contentType = contentTypeOf(list, contentTypeId);
  const fields = useMemo(() => withTitle(contentType?.fields ?? list.columns), [contentType, list.columns]);
  // The version the user's edits start from: its values are compared and its ETag is sent with the save.
  const [base, setBase] = useState(item);
  const original = useMemo(() => (base ? fieldsOf(base) : {}), [base]);
  const [values, setValues] = useState<Record<string, unknown>>(() => ({
    ...defaults(fields, !item),
    ...(item ? {} : initialValues),
    ...original,
  }));
  // A newer version from elsewhere (live update, workflow, smart folder) replaces the values unless the user has
  // edited them; then the edits stay on the version they started from and the save's If-Match catches the conflict.
  // Adjusted during render, as React recommends for prop changes.
  const [seen, setSeen] = useState(item);
  if (seen !== item) {
    setSeen(item);
    if (item && Object.keys(changes(original, values)).length === 0) {
      setBase(item);
      setValues(fieldsOf(item));
    }
  }
  const rebase = (latest: ItemResponse) => {
    setBase(latest);
    setValues(fieldsOf(latest));
  };
  const [conflict, setConflict] = useState(false);
  const [theirs, setTheirs] = useState<Record<string, unknown> | null>(null);
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
      const current = force ? await items.byItemId(item.id!).get() : base;
      return (await items.byItemId(item.id!).patch({ fields: fieldValues(body) }, ifMatch(current)))!;
    },
    onMutate: () => {
      setErrors({});
      setConflict(false);
    },
    onSuccess: async (saved) => {
      if (item) rebase(saved);
      queryClient.setQueryData(keys.item(workspaceId, list.id!, saved.id!), saved);
      await queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, list.id!) });
      onSaved(saved);
    },
    onError: (error) => {
      if (isStatus(error, 412)) {
        setConflict(true);
        if (item) {
          void items
            .byItemId(item.id!)
            .get()
            .then((latest) => {
              if (latest) setTheirs(fieldsOf(latest));
            });
        }
      }
      setErrors(validationErrors(error));
    },
  });

  const reload = async () => {
    const latest = await queryClient.fetchQuery({
      queryKey: keys.item(workspaceId, list.id!, item!.id!),
      staleTime: 0,
      queryFn: async () => (await items.byItemId(item!.id!).get())!,
    });
    rebase(latest);
    setConflict(false);
    setTheirs(null);
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
        <fieldset disabled={readOnly} className="flex-1 space-y-4 overflow-y-auto px-5 py-4">
          {conflict && (
            <Alert tone="warning" className="flex flex-col gap-2">
              <span>Someone else changed this item after you opened it.</span>
              {theirs && (
                <ul className="flex flex-col gap-1.5 text-[13px]">
                  {fields
                    .filter((field) => JSON.stringify(theirs[field.name!]) !== JSON.stringify(values[field.name!]))
                    .map((field) => (
                      <li key={field.name} className="flex flex-wrap items-baseline gap-x-2">
                        <span className="font-medium">{fieldLabel(field)}</span>
                        <span className="text-muted">
                          theirs <FieldValue field={field} value={theirs[field.name!]} />
                        </span>
                        <span>
                          yours <FieldValue field={field} value={values[field.name!]} />
                        </span>
                      </li>
                    ))}
                </ul>
              )}
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
                {contentType?.key === 'note' && field.name === 'body' ? (
                  <NoteEditor
                    id={id}
                    value={String(values.body ?? '')}
                    onChange={(value) => setValues((current) => ({ ...current, body: value }))}
                    context={item ? { workspaceId, listId: list.id!, itemId: item.id! } : undefined}
                  />
                ) : (
                  <FieldEditor
                    id={id}
                    field={field}
                    value={values[field.name!]}
                    invalid={!!messages}
                    onChange={(value) => setValues((current) => withLinkedValues(current, field.name!, value))}
                  />
                )}
                {field.description && !messages && <p className="text-xs text-muted">{field.description}</p>}
                {messages && <p className="text-xs text-danger">{messages.join(' ')}</p>}
              </div>
            );
          })}
        </fieldset>
        <div className="flex items-center justify-end gap-2 border-t bg-surface px-5 py-3">
          {readOnly && <span className="mr-auto text-xs text-muted">You can read this item.</span>}
          {item && dirty && <span className="mr-auto text-xs text-muted">Unsaved changes</span>}
          {onCancel && <Button onClick={onCancel}>{item ? 'Close' : 'Cancel'}</Button>}
          {!readOnly && item && dirty && (
            <Button onClick={() => (item ? rebase(item) : undefined)} disabled={save.isPending}>
              Discard
            </Button>
          )}
          {!readOnly && (
            <Button type="submit" variant="primary" disabled={!dirty || save.isPending}>
              {save.isPending ? <Spinner className="text-current" /> : <Save />}
              {item ? 'Save' : 'Create'}
            </Button>
          )}
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
