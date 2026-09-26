import type { ContentTypeResponse, FieldDefinitionDto, FieldSearchWeight } from '@paperdotnet/client';
import { ifMatch, jsonNode, jsonOf } from '@paperdotnet/client';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowDown, ArrowUp, ChevronRight, Plus, Shapes, Trash2 } from 'lucide-react';
import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { listsQuery, workspacesQuery } from '@/api/queries';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert } from '@/components/ui/feedback';
import { Input, Label, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';
import { contentTypesQuery, fieldTypesQuery, termSetsQuery } from './queries';

/** Readable names of the built-in field types (LST-03); extension types show their name. */
export const fieldTypeLabels: Record<string, string> = {
  text: 'Text',
  note: 'Long text',
  email: 'Email',
  url: 'Link',
  number: 'Number',
  currency: 'Money',
  boolean: 'Yes/no',
  date: 'Date',
  dateTime: 'Date and time',
  choice: 'Choice',
  person: 'Person',
  lookup: 'Lookup (item of a list)',
  managedMetadata: 'Managed metadata (terms)',
  keywords: 'Keywords (tags)',
};

interface FieldDraft {
  key: string;
  /** Saved fields keep their name and type (the server refuses changes). */
  saved: boolean;
  name: string;
  displayName: string;
  type: string;
  description: string;
  required: boolean;
  allowMultiple: boolean;
  maxLength: string;
  minimum: string;
  maximum: string;
  choices: string;
  lookupListId: string;
  currencyCode: string;
  termSetId: string;
  defaultValue: string;
  search: FieldSearchWeight | '';
}

let counter = 0;
const text = (v: number | null | undefined) => (v === null || v === undefined ? '' : String(v));
const num = (v: string) => (v.trim() === '' ? null : Number(v));

function draftOf(field: FieldDefinitionDto): FieldDraft {
  const defaultValue = jsonOf(field.defaultValue);
  return {
    key: `f${++counter}`,
    saved: true,
    name: field.name ?? '',
    displayName: field.displayName ?? '',
    type: field.type ?? 'text',
    description: field.description ?? '',
    required: !!field.required,
    allowMultiple: !!field.allowMultiple,
    maxLength: text(field.maxLength),
    minimum: text(field.minimum),
    maximum: text(field.maximum),
    choices: (field.choices ?? []).join('\n'),
    lookupListId: field.lookupListId ?? '',
    currencyCode: field.currencyCode ?? '',
    termSetId: field.termSetId ?? '',
    defaultValue:
      defaultValue === undefined || defaultValue === null
        ? ''
        : typeof defaultValue === 'string'
          ? defaultValue
          : JSON.stringify(defaultValue),
    search: (field.search as FieldSearchWeight | undefined) ?? '',
  };
}

/** "Due date" → "dueDate": the stored name of a new field. */
export function fieldNameOf(label: string): string {
  const words = label
    .normalize('NFKD')
    .replace(/[^\w\s]/g, '')
    .trim()
    .split(/\s+/)
    .filter(Boolean);
  const name = words
    .map((w, i) => (i === 0 ? w.toLowerCase() : w[0]!.toUpperCase() + w.slice(1).toLowerCase()))
    .join('');
  return /^[a-z]/.test(name) ? name : `field${name}`;
}

function defaultValueOf(field: FieldDraft): unknown {
  const value = field.defaultValue.trim();
  if (!value) return null;
  if (field.type === 'number' || field.type === 'currency') return Number(value);
  if (field.type === 'boolean') return value === 'true';
  return value;
}

function dtoOf(field: FieldDraft): FieldDefinitionDto {
  return {
    name: field.name,
    displayName: field.displayName.trim() || null,
    type: field.type,
    description: field.description.trim() || null,
    required: field.required,
    allowMultiple: field.allowMultiple,
    maxLength: num(field.maxLength),
    minimum: num(field.minimum),
    maximum: num(field.maximum),
    choices:
      field.type === 'choice'
        ? field.choices
            .split('\n')
            .map((c) => c.trim())
            .filter(Boolean)
        : null,
    lookupListId: field.type === 'lookup' ? field.lookupListId || null : null,
    currencyCode: field.type === 'currency' ? field.currencyCode.trim().toUpperCase() || null : null,
    termSetId: field.type === 'managedMetadata' ? field.termSetId || null : null,
    defaultValue: defaultValueOf(field) === null ? null : jsonNode(defaultValueOf(field)),
    search: field.search || null,
  };
}

/**
 * Creates or changes a content type (LST-02): its name and fields. Content types belong to the organization, so a
 * change applies to every list that uses it; `usedBy` names them.
 */
export function ContentTypeEditor({
  contentType,
  usedBy,
  canManage,
  onSaved,
  onClose,
}: {
  /** Undefined for a new content type. */
  contentType?: ContentTypeResponse;
  usedBy: string[];
  canManage: boolean;
  onSaved?: (contentType: ContentTypeResponse) => void | Promise<void>;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(contentType?.name ?? '');
  const [description, setDescription] = useState(contentType?.description ?? '');
  const [fields, setFields] = useState<FieldDraft[]>(() => (contentType?.fields ?? []).map(draftOf));
  const [open, setOpen] = useState<string>();
  const readOnly = !canManage || !!contentType?.extensionId;
  const save = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const body = { name: name.trim(), description: description.trim() || null, fields: fields.map(dtoOf) };
      return (
        contentType
          ? await api.v10.contentTypes.byId(contentType.id!).put(body, ifMatch(contentType))
          : await api.v10.contentTypes.post(body)
      )!;
    },
    onSuccess: async (saved) => {
      toast.success(contentType ? 'Content type saved.' : 'Content type created.');
      await queryClient.invalidateQueries({ queryKey: contentTypesQuery.queryKey });
      // Lists show the fields of their content types.
      await queryClient.invalidateQueries({ queryKey: ['workspaces'] });
      await onSaved?.(saved);
      onClose();
    },
  });
  const addField = () => {
    const key = `f${++counter}`;
    setFields([...fields, { ...draftOf({ type: 'text' }), key, saved: false, name: '', displayName: '' }]);
    setOpen(key);
  };
  const replace = (key: string, patch: Partial<FieldDraft>) =>
    setFields(fields.map((f) => (f.key === key ? { ...f, ...patch } : f)));
  const move = (index: number, by: number) => {
    const next = [...fields];
    const [field] = next.splice(index, 1);
    next.splice(index + by, 0, field!);
    setFields(next);
  };
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  const duplicate = new Set(fields.map((f) => f.name).filter((n, i, all) => n && all.indexOf(n) !== i));

  return (
    <Sheet open onOpenChange={(value) => !value && onClose()}>
      <SheetContent className="md:w-[min(720px,94vw)]">
        <form onSubmit={onSubmit} className="flex min-h-0 flex-1 flex-col">
          <header className="flex items-center gap-2 border-b px-5 py-3">
            <Shapes className="size-5 text-accent" />
            <div className="min-w-0 flex-1">
              <SheetTitle className="truncate font-semibold">
                {contentType ? contentType.name : 'New content type'}
              </SheetTitle>
              <SheetDescription className="text-xs text-muted">
                {contentType?.extensionId
                  ? `Managed by the extension ${contentType.extensionId}.`
                  : 'A named set of fields that lists and libraries share.'}
              </SheetDescription>
            </div>
            <SheetClose />
          </header>
          <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-5">
            {usedBy.length > 1 && (
              <Alert tone="warning">
                Changes apply to all {usedBy.length} lists that use this content type: {usedBy.join(', ')}.
              </Alert>
            )}
            <fieldset disabled={readOnly} className="contents">
              <Row label="Name">
                {(id) => (
                  <Input
                    id={id}
                    required
                    maxLength={200}
                    disabled={!!contentType?.isBuiltIn}
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                  />
                )}
              </Row>
              <Row label="Description">
                {(id) => (
                  <Textarea id={id} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
                )}
              </Row>
              <section className="flex flex-col gap-2">
                <h3 className="text-[13px] font-semibold">Fields</h3>
                <p className="text-xs text-muted">
                  Every item has a Title as well. A field’s name and type stay once saved.
                </p>
                <ol className="flex flex-col gap-1.5">
                  {fields.map((field, index) => (
                    <li key={field.key}>
                      <FieldCard
                        field={field}
                        expanded={open === field.key}
                        duplicate={duplicate.has(field.name)}
                        readOnly={readOnly}
                        onToggle={() => setOpen(open === field.key ? undefined : field.key)}
                        onChange={(patch) => replace(field.key, patch)}
                        onRemove={() => setFields(fields.filter((f) => f.key !== field.key))}
                        onMoveUp={index > 0 ? () => move(index, -1) : undefined}
                        onMoveDown={index < fields.length - 1 ? () => move(index, 1) : undefined}
                      />
                    </li>
                  ))}
                </ol>
                {!readOnly && (
                  <Button size="sm" variant="ghost" className="self-start text-accent" onClick={addField}>
                    <Plus /> Add a field
                  </Button>
                )}
              </section>
            </fieldset>
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </div>
          <footer className="flex justify-end gap-2 border-t px-5 py-3">
            <Button type="button" onClick={onClose}>
              {readOnly ? 'Close' : 'Cancel'}
            </Button>
            {!readOnly && (
              <Button
                type="submit"
                variant="primary"
                disabled={!name.trim() || duplicate.size > 0 || fields.some((f) => !f.name) || save.isPending}
              >
                {contentType ? 'Save' : 'Create content type'}
              </Button>
            )}
          </footer>
        </form>
      </SheetContent>
    </Sheet>
  );
}

function FieldCard({
  field,
  expanded,
  duplicate,
  readOnly,
  onToggle,
  onChange,
  onRemove,
  onMoveUp,
  onMoveDown,
}: {
  field: FieldDraft;
  expanded: boolean;
  duplicate: boolean;
  readOnly: boolean;
  onToggle: () => void;
  onChange: (patch: Partial<FieldDraft>) => void;
  onRemove: () => void;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
}) {
  const { data: fieldTypes } = useQuery(fieldTypesQuery);
  const { data: termSets } = useQuery(termSetsQuery);
  const { data: workspaces } = useQuery(workspacesQuery);
  const lists = useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) }).flatMap((q) => q.data ?? []);
  const supportsMultiple = fieldTypes?.find((t) => t.name === field.type)?.supportsMultiple;
  const label = field.displayName || field.name || 'New field';
  return (
    <section aria-label={`Field ${label}`} className={cn('rounded-lg border bg-surface', duplicate && 'border-danger')}>
      <div className="flex items-center gap-2 px-3 py-2">
        <button
          type="button"
          aria-expanded={expanded}
          onClick={onToggle}
          className="flex min-w-0 flex-1 items-center gap-2 text-left"
        >
          <ChevronRight className={cn('size-4 text-muted transition-transform', expanded && 'rotate-90')} />
          <span className="truncate text-[13px] font-medium">{label}</span>
          <Badge>{fieldTypeLabels[field.type] ?? field.type}</Badge>
          {field.required && <Badge tone="warning">Required</Badge>}
          {duplicate && <Badge tone="danger">Same name twice</Badge>}
        </button>
        {!readOnly && (
          <div className="flex gap-0.5">
            <Button size="icon" variant="ghost" aria-label={`Move ${label} up`} disabled={!onMoveUp} onClick={onMoveUp}>
              <ArrowUp />
            </Button>
            <Button
              size="icon"
              variant="ghost"
              aria-label={`Move ${label} down`}
              disabled={!onMoveDown}
              onClick={onMoveDown}
            >
              <ArrowDown />
            </Button>
            <Button size="icon" variant="ghost" aria-label={`Remove ${label}`} onClick={onRemove}>
              <Trash2 />
            </Button>
          </div>
        )}
      </div>
      {expanded && (
        <div className="grid gap-3 border-t p-3 sm:grid-cols-2">
          <Row label="Display name">
            {(id) => (
              <Input
                id={id}
                required
                value={field.displayName}
                onChange={(e) =>
                  onChange({
                    displayName: e.target.value,
                    ...(field.saved ? {} : { name: fieldNameOf(e.target.value) }),
                  })
                }
              />
            )}
          </Row>
          <Row label="Name" hint={field.saved ? 'Used in filters and the API.' : 'Set from the display name.'}>
            {(id) => (
              <Input
                id={id}
                className="font-mono text-xs"
                disabled={field.saved}
                pattern="[a-zA-Z][a-zA-Z0-9_]*"
                value={field.name}
                onChange={(e) => onChange({ name: e.target.value })}
              />
            )}
          </Row>
          <Row label="Type">
            {(id) => (
              <Select
                id={id}
                disabled={field.saved}
                value={field.type}
                onChange={(e) => onChange({ type: e.target.value })}
              >
                {(fieldTypes ?? [{ name: field.type }]).map((t) => (
                  <option key={t.name} value={t.name!}>
                    {fieldTypeLabels[t.name!] ?? t.name}
                  </option>
                ))}
              </Select>
            )}
          </Row>
          <div className="flex flex-col justify-end gap-1.5 pb-1">
            <label className="flex items-center gap-2 text-[13px]">
              <Checkbox checked={field.required} onChange={(e) => onChange({ required: e.target.checked })} /> Required
            </label>
            {supportsMultiple && (
              <label className="flex items-center gap-2 text-[13px]">
                <Checkbox
                  checked={field.allowMultiple}
                  disabled={field.saved}
                  onChange={(e) => onChange({ allowMultiple: e.target.checked })}
                />
                Several values
              </label>
            )}
          </div>
          <Row label="Description" className="sm:col-span-2">
            {(id) => (
              <Input id={id} value={field.description} onChange={(e) => onChange({ description: e.target.value })} />
            )}
          </Row>
          {(field.type === 'text' || field.type === 'note') && (
            <Row label="Maximum length">
              {(id) => (
                <Input
                  id={id}
                  type="number"
                  min={1}
                  value={field.maxLength}
                  onChange={(e) => onChange({ maxLength: e.target.value })}
                />
              )}
            </Row>
          )}
          {(field.type === 'number' || field.type === 'currency') && (
            <>
              <Row label="Minimum">
                {(id) => (
                  <Input
                    id={id}
                    type="number"
                    step="any"
                    value={field.minimum}
                    onChange={(e) => onChange({ minimum: e.target.value })}
                  />
                )}
              </Row>
              <Row label="Maximum">
                {(id) => (
                  <Input
                    id={id}
                    type="number"
                    step="any"
                    value={field.maximum}
                    onChange={(e) => onChange({ maximum: e.target.value })}
                  />
                )}
              </Row>
            </>
          )}
          {field.type === 'currency' && (
            <Row label="Currency" hint="ISO code, e.g. EUR">
              {(id) => (
                <Input
                  id={id}
                  maxLength={3}
                  className="w-24 uppercase"
                  value={field.currencyCode}
                  onChange={(e) => onChange({ currencyCode: e.target.value })}
                />
              )}
            </Row>
          )}
          {field.type === 'choice' && (
            <Row label="Choices" hint="One per line." className="sm:col-span-2">
              {(id) => (
                <Textarea
                  id={id}
                  rows={4}
                  value={field.choices}
                  onChange={(e) => onChange({ choices: e.target.value })}
                />
              )}
            </Row>
          )}
          {field.type === 'lookup' && (
            <Row label="Items of" className="sm:col-span-2">
              {(id) => (
                <Select
                  id={id}
                  required
                  value={field.lookupListId}
                  onChange={(e) => onChange({ lookupListId: e.target.value })}
                >
                  <option value="">Choose a list…</option>
                  {lists.map((l) => (
                    <option key={l.id} value={l.id!}>
                      {(workspaces?.find((w) => w.id === l.workspaceId)?.name ?? '') + ' › ' + l.name}
                    </option>
                  ))}
                </Select>
              )}
            </Row>
          )}
          {field.type === 'managedMetadata' && (
            <Row label="Term set" className="sm:col-span-2">
              {(id) => (
                <Select
                  id={id}
                  required
                  disabled={field.saved}
                  value={field.termSetId}
                  onChange={(e) => onChange({ termSetId: e.target.value })}
                >
                  <option value="">Choose a term set…</option>
                  {(termSets ?? []).map((s) => (
                    <option key={s.id} value={s.id!}>
                      {s.name}
                    </option>
                  ))}
                </Select>
              )}
            </Row>
          )}
          {['text', 'number', 'currency', 'choice', 'boolean', 'email', 'url'].includes(field.type) && (
            <Row label="Default value">
              {(id) =>
                field.type === 'boolean' ? (
                  <Select
                    id={id}
                    value={field.defaultValue}
                    onChange={(e) => onChange({ defaultValue: e.target.value })}
                  >
                    <option value="">None</option>
                    <option value="true">Yes</option>
                    <option value="false">No</option>
                  </Select>
                ) : (
                  <Input
                    id={id}
                    value={field.defaultValue}
                    onChange={(e) => onChange({ defaultValue: e.target.value })}
                  />
                )
              }
            </Row>
          )}
          <Row label="In search" hint="How much a match in this field counts.">
            {(id) => (
              <Select
                id={id}
                value={field.search}
                onChange={(e) => onChange({ search: e.target.value as FieldSearchWeight | '' })}
              >
                <option value="">Default</option>
                <option value="high">Important</option>
                <option value="normal">Normal</option>
                <option value="none">Not searched</option>
              </Select>
            )}
          </Row>
        </div>
      )}
    </section>
  );
}

function Row({
  label,
  hint,
  className,
  children,
}: {
  label: string;
  hint?: ReactNode;
  className?: string;
  children: (id: string) => ReactNode;
}) {
  const id = useId();
  return (
    <div className={cn('flex min-w-0 flex-col gap-1', className)}>
      <Label htmlFor={id}>{label}</Label>
      {children(id)}
      {hint && <p className="text-xs text-muted">{hint}</p>}
    </div>
  );
}
