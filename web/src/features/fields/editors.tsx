import { fieldsOf } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDeferredValue, useState } from 'react';
import { api } from '@/api/client';
import { Avatar } from '@/components/ui/avatar';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Input, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { listBuilder, odataString } from '@/features/lists/queries';
import { useFormat } from '@/lib/preferences';
import { fromZonedInput, toZonedInput } from '@/lib/zoned';
import { userName, usersQuery } from './directory';
import { termLabel, useListWorkspace, useValueNames } from './lookups';
import { choiceLabel, idsOf, type FieldDefinition } from './values';

export interface EditorProps {
  id: string;
  field: FieldDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
  invalid?: boolean;
}

/** Custom widgets (pickers) are named by their visible label: `${id}-label`. */
const labelOf = (id: string) => `${id}-label`;

/** The editor of a field value, by field type (unknown types from extensions edit their JSON). */
export function FieldEditor(props: EditorProps) {
  const { id, field, value, onChange, invalid } = props;
  const common = { id, 'aria-invalid': invalid || undefined, required: !!field.required };
  const text = value == null ? '' : String(value);

  switch (field.type) {
    case 'text':
      return (
        <Input
          {...common}
          maxLength={field.maxLength ?? undefined}
          value={text}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case 'note':
      return (
        <Textarea
          {...common}
          rows={4}
          maxLength={field.maxLength ?? undefined}
          value={text}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case 'email':
      return <Input {...common} type="email" value={text} onChange={(e) => onChange(e.target.value)} />;
    case 'url':
      return (
        <Input {...common} type="url" placeholder="https://" value={text} onChange={(e) => onChange(e.target.value)} />
      );
    case 'number':
    case 'currency':
      return (
        <div className="flex items-center gap-2">
          <Input
            {...common}
            type="number"
            inputMode="decimal"
            step="any"
            min={field.minimum ?? undefined}
            max={field.maximum ?? undefined}
            value={text}
            onChange={(e) => onChange(e.target.value === '' ? null : e.target.valueAsNumber)}
          />
          {field.type === 'currency' && (
            <span className="text-xs font-medium text-muted">{field.currencyCode ?? 'EUR'}</span>
          )}
        </div>
      );
    case 'boolean':
      return (
        <label className="flex h-9 items-center gap-2 text-sm">
          <Checkbox id={id} checked={value === true} onChange={(e) => onChange(e.target.checked)} />
          {field.displayName ?? field.name}
        </label>
      );
    case 'date':
      return (
        <Input {...common} type="date" value={text.slice(0, 10)} onChange={(e) => onChange(e.target.value || null)} />
      );
    case 'dateTime':
      return <DateTimeEditor {...props} />;
    case 'choice':
      return field.allowMultiple ? (
        <div id={id} className="flex flex-wrap gap-1.5" role="group" aria-labelledby={labelOf(id)}>
          {(field.choices ?? []).map((choice) => {
            const selected = idsOf(value).includes(choice);
            return (
              <button
                key={choice}
                type="button"
                aria-pressed={selected}
                onClick={() =>
                  onChange(selected ? idsOf(value).filter((v) => v !== choice) : [...idsOf(value), choice])
                }
                className={
                  selected
                    ? 'rounded-full border border-accent bg-accent-soft px-2.5 py-1 text-xs font-medium text-accent'
                    : 'rounded-full border px-2.5 py-1 text-xs hover:bg-surface-muted'
                }
              >
                {choiceLabel(choice)}
              </button>
            );
          })}
        </div>
      ) : (
        <Select {...common} value={text} onChange={(e) => onChange(e.target.value || null)}>
          <option value="">{field.required ? 'Choose…' : '—'}</option>
          {(field.choices ?? []).map((choice) => (
            <option key={choice} value={choice}>
              {choiceLabel(choice)}
            </option>
          ))}
        </Select>
      );
    case 'person':
      return <PersonEditor {...props} />;
    case 'lookup':
      return <LookupEditor {...props} />;
    case 'managedMetadata':
      return <TermEditor {...props} />;
    case 'keywords':
      return <KeywordsEditor {...props} />;
    default:
      return <JsonEditor {...props} />;
  }
}

function DateTimeEditor({ id, field, value, onChange, invalid }: EditorProps) {
  const format = useFormat();
  const zone = format.preferences.timeZone;
  return (
    <div className="flex items-center gap-2">
      <Input
        id={id}
        type="datetime-local"
        aria-invalid={invalid || undefined}
        required={!!field.required}
        value={toZonedInput(value as string | null, zone)}
        onChange={(e) => onChange(e.target.value ? fromZonedInput(e.target.value, zone) : null)}
      />
      <span className="shrink-0 text-xs text-muted">{zone}</span>
    </div>
  );
}

const selectedValues = (field: FieldDefinition, options: ComboboxOption[]) =>
  field.allowMultiple ? options.map((o) => o.value) : (options[0]?.value ?? null);

function PersonEditor({ id, field, value, onChange, invalid }: EditorProps) {
  const { data: users, isPending } = useQuery(usersQuery);
  const option = (userId: string): ComboboxOption => {
    const user = users?.find((u) => u.id === userId);
    const name = userName(user, userId);
    return {
      value: userId,
      label: name,
      hint: user?.email ?? undefined,
      icon: <Avatar name={name} className="size-5 text-[9px]" />,
    };
  };
  return (
    <Combobox
      id={id}
      aria-labelledby={labelOf(id)}
      aria-invalid={invalid}
      multiple={!!field.allowMultiple}
      loading={isPending}
      placeholder="Choose people…"
      selected={idsOf(value).map(option)}
      options={(users ?? []).filter((u) => !u.isDisabled).map((u) => option(u.id!))}
      onChange={(options) => onChange(selectedValues(field, options))}
    />
  );
}

function LookupEditor({ id, field, value, onChange, invalid }: EditorProps) {
  const names = useValueNames();
  const workspaceId = useListWorkspace(field.lookupListId);
  const [search, setSearch] = useState('');
  const text = useDeferredValue(search.trim());
  const { data, isFetching } = useQuery({
    queryKey: ['lookupSearch', field.lookupListId, text],
    enabled: !!workspaceId && !!field.lookupListId,
    queryFn: async () =>
      (
        await listBuilder(workspaceId!, field.lookupListId!).items.get({
          queryParameters: {
            filter: text ? `contains(tolower(fields/title),${odataString(text.toLowerCase())})` : undefined,
            orderby: 'fields/title',
            select: 'title',
            top: 30,
          },
        })
      )?.value ?? [],
  });
  const options = (data ?? []).map((item) => ({ value: item.id!, label: String(fieldsOf(item).title ?? 'Untitled') }));
  const known = new Map(options.map((o) => [o.value, o.label]));
  return (
    <Combobox
      id={id}
      aria-labelledby={labelOf(id)}
      aria-invalid={invalid}
      multiple={!!field.allowMultiple}
      loading={isFetching}
      onSearch={setSearch}
      selected={idsOf(value).map((v) => ({ value: v, label: known.get(v) ?? names.items.get(v) ?? '…' }))}
      options={options}
      onChange={(selected) => onChange(selectedValues(field, selected))}
    />
  );
}

function TermEditor({ id, field, value, onChange, invalid }: EditorProps) {
  const format = useFormat();
  const names = useValueNames();
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const text = useDeferredValue(search.trim());
  const setId = field.termSetId!;
  const { data: set } = useQuery({
    queryKey: ['termSets', setId],
    queryFn: async () => (await api.v10.termStore.sets.bySetId(setId).get())!,
    staleTime: 5 * 60_000,
  });
  const { data, isFetching } = useQuery({
    queryKey: ['termSets', setId, 'terms', text],
    queryFn: async () =>
      (
        await api.v10.termStore.sets
          .bySetId(setId)
          .terms.get({ queryParameters: { search: text || undefined, top: 50 } })
      )?.value ?? [],
  });
  const create = useMutation({
    mutationFn: (name: string) => api.v10.termStore.sets.bySetId(setId).terms.post({ name }),
    onSuccess: async (term) => {
      await queryClient.invalidateQueries({ queryKey: ['termSets', setId] });
      if (term?.id) onChange(field.allowMultiple ? [...idsOf(value), term.id] : term.id);
    },
  });
  const option = (term: { id?: string | null; color?: string | null }, label?: string): ComboboxOption => ({
    value: term.id!,
    label: label ?? '…',
    color: term.color,
  });
  return (
    <Combobox
      id={id}
      aria-labelledby={labelOf(id)}
      aria-invalid={invalid}
      multiple={!!field.allowMultiple}
      loading={isFetching}
      onSearch={setSearch}
      onCreate={set?.isOpen ? (name) => create.mutate(name) : undefined}
      selected={idsOf(value).map((termId) => {
        const term = names.terms.get(termId) ?? data?.find((t) => t.id === termId);
        return option({ id: termId, color: term?.color }, termLabel(term, format.preferences.language));
      })}
      options={(data ?? []).map((t) => option(t, termLabel(t, format.preferences.language)))}
      onChange={(selected) => onChange(selectedValues(field, selected))}
    />
  );
}

function KeywordsEditor({ id, value, onChange, invalid }: EditorProps) {
  const names = useValueNames();
  const [search, setSearch] = useState('');
  const text = useDeferredValue(search.trim());
  const [added, setAdded] = useState(new Map<string, string>());
  const { data, isFetching } = useQuery({
    queryKey: ['keywords', text],
    queryFn: async () =>
      (await api.v10.termStore.keywords.get({ queryParameters: { search: text || undefined } })) ?? [],
  });
  const add = useMutation({
    mutationFn: (name: string) => api.v10.termStore.keywords.post({ name }),
    onSuccess: (term) => {
      if (!term?.id) return;
      setAdded((current) => new Map(current).set(term.id!, term.name ?? ''));
      onChange([...idsOf(value).filter((v) => v !== term.id), term.id]);
    },
  });
  return (
    <Combobox
      id={id}
      aria-labelledby={labelOf(id)}
      aria-invalid={invalid}
      multiple
      loading={isFetching || add.isPending}
      placeholder="Add keywords…"
      onSearch={setSearch}
      onCreate={(name) => add.mutate(name)}
      selected={idsOf(value).map((termId) => ({
        value: termId,
        label: names.terms.get(termId)?.name ?? added.get(termId) ?? data?.find((t) => t.id === termId)?.name ?? '…',
      }))}
      options={(data ?? []).map((t) => ({ value: t.id!, label: t.name ?? '' }))}
      onChange={(selected) => onChange(selected.map((o) => o.value))}
    />
  );
}

function JsonEditor({ id, value, onChange, invalid }: EditorProps) {
  const [text, setText] = useState(() => (value == null ? '' : JSON.stringify(value)));
  const [error, setError] = useState(false);
  return (
    <Input
      id={id}
      aria-invalid={invalid || error || undefined}
      className="font-mono text-xs"
      value={text}
      onChange={(e) => {
        setText(e.target.value);
        try {
          onChange(e.target.value === '' ? null : JSON.parse(e.target.value));
          setError(false);
        } catch {
          setError(true);
        }
      }}
    />
  );
}
