import { fieldsOf } from '@paperdotnet/client';
import type { FieldProps } from '@rjsf/utils';
import { useQuery } from '@tanstack/react-query';
import { useDeferredValue, useState } from 'react';
import { api } from '@/api/client';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Alert } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';

interface DomainOptions {
  kind: 'relationship' | 'terms' | 'keywords';
  relationshipType?: string;
  groupId?: string;
  termSetId?: string;
  termIds?: string[];
}

export function DomainSelection({
  schema,
  formData,
  onChange,
  fieldPathId,
  disabled,
  readonly,
  required,
  rawErrors,
}: FieldProps) {
  const config = schema['x-paperdotnet'] as DomainOptions;
  const multiple = schema.type === 'array';
  const selected = (Array.isArray(formData) ? formData : typeof formData === 'string' ? [formData] : []) as string[];
  const [search, setSearch] = useState('');
  const text = useDeferredValue(search.trim());
  const [chosenSet, setChosenSet] = useState('');
  const [labels, setLabels] = useState<Record<string, string>>({});
  const sets = useQuery({
    queryKey: ['workflowDomainSets', config.groupId],
    enabled:
      config.kind !== 'relationship' &&
      (!!config.groupId || (config.kind === 'terms' && !config.termSetId && !config.termIds)),
    queryFn: async () => {
      const values = [];
      let page = await api.v10.termStore.sets.get({ queryParameters: { groupId: config.groupId, top: 100 } });
      while (page) {
        values.push(...(page.value ?? []));
        if (!page.odataNextLink) break;
        page = await api.v10.termStore.sets.withUrl(page.odataNextLink).get();
      }
      return values;
    },
    staleTime: 5 * 60_000,
  });
  const setId = config.termSetId ?? chosenSet;
  const options = useQuery({
    queryKey: ['workflowDomainOptions', config, setId, text],
    enabled:
      (!config.groupId || sets.isSuccess) &&
      (config.kind === 'relationship' ||
        !!setId ||
        !!config.termIds ||
        (config.kind === 'keywords' && !config.groupId)),
    queryFn: async (): Promise<ComboboxOption[]> => {
      if (config.kind === 'relationship') {
        if (!text) return [];
        const page = await api.v10.items.get({ queryParameters: { q: text, top: 30 } });
        return (page?.value ?? []).map((entry) => ({
          value: entry.item!.id!,
          label: String(fieldsOf(entry.item).title ?? 'Untitled'),
        }));
      }
      const terms = config.termIds
        ? await api.v10.termStore.terms.get({ queryParameters: { ids: config.termIds.join(',') } })
        : setId && config.kind !== 'keywords'
          ? (
              await api.v10.termStore.sets
                .bySetId(setId)
                .terms.get({ queryParameters: { search: text || undefined, top: 50 } })
            )?.value
          : await api.v10.termStore.keywords.get({ queryParameters: { search: text || undefined } });
      return (terms ?? [])
        .filter(
          (term) =>
            !term.isDeprecated &&
            (!setId || term.termSetId === setId) &&
            (!config.groupId || sets.data?.some((set) => set.id === term.termSetId)),
        )
        .map((term) => ({ value: term.id!, label: term.name ?? '', color: term.color }));
    },
    staleTime: 30_000,
  });
  const names = useQuery({
    queryKey: ['workflowDomainNames', config.kind, selected],
    enabled: selected.length > 0,
    queryFn: async (): Promise<ComboboxOption[]> =>
      config.kind === 'relationship'
        ? Promise.all(
            selected.map(async (id) => {
              const entry = await api.v10.items.byItemId(id).get();
              return { value: id, label: String(fieldsOf(entry?.item).title ?? id) };
            }),
          )
        : ((await api.v10.termStore.terms.get({ queryParameters: { ids: selected.join(',') } })) ?? []).map((term) => ({
            value: term.id!,
            label: term.name ?? '',
          })),
    staleTime: 5 * 60_000,
  });
  const known = new Map([...(names.data ?? []), ...(options.data ?? [])].map((option) => [option.value, option.label]));
  const id = fieldPathId.$id;
  return (
    <div className="flex flex-col gap-2">
      <Label id={`${id}-label`}>
        {schema.title ?? fieldPathId.path.at(-1)}
        {required ? ' *' : ''}
      </Label>
      {schema.description && <p className="text-sm text-muted">{schema.description}</p>}
      {sets.data && !config.termSetId && !config.termIds && (
        <Select
          aria-label="Term set"
          disabled={disabled || readonly}
          value={chosenSet}
          onChange={(event) => setChosenSet(event.target.value)}
        >
          <option value="">Choose a term set…</option>
          {sets.data.map((set) => (
            <option key={set.id} value={set.id!}>
              {set.name}
            </option>
          ))}
        </Select>
      )}
      <Combobox
        id={id}
        aria-labelledby={`${id}-label`}
        multiple={multiple}
        disabled={disabled || readonly}
        loading={options.isFetching}
        aria-invalid={!!rawErrors?.length}
        placeholder={config.kind === 'relationship' ? 'Search relationship targets…' : 'Choose terms…'}
        selected={selected.map((value) => ({ value, label: known.get(value) ?? labels[value] ?? value }))}
        options={options.data ?? []}
        onSearch={config.termIds ? undefined : setSearch}
        onChange={(values) => {
          setLabels((previous) => ({
            ...previous,
            ...Object.fromEntries(values.map((option) => [option.value, option.label])),
          }));
          onChange(multiple ? values.map((option) => option.value) : values[0]?.value, fieldPathId.path);
        }}
      />
      {(sets.isError || options.isError || names.isError) && <Alert>Could not load choices. Try again.</Alert>}
      {rawErrors?.map((error) => (
        <p key={error} className="text-sm text-danger">
          {error}
        </p>
      ))}
    </div>
  );
}
