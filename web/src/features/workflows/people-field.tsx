import type { UserResponse } from '@paperdotnet/client';
import type { FieldProps } from '@rjsf/utils';
import { useQuery } from '@tanstack/react-query';
import { UserRound, Users } from 'lucide-react';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Alert } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { assignablePeopleQuery, groupsQuery, userName, usersByIdQuery } from '@/features/fields/directory';

interface PeopleOptions {
  kind: 'people';
  /** An identity group: only its members (groups inside it included) can be picked. */
  memberOf?: string;
  people?: boolean;
  groups?: boolean;
}

/** A people input (`kind: "people"`): the choices and the names of selected ids come from the directory API. */
export function PeopleSelection({
  schema,
  formData,
  onChange,
  fieldPathId,
  disabled,
  readonly,
  required,
  rawErrors,
}: FieldProps) {
  const config = schema['x-paperdotnet'] as PeopleOptions;
  const allowPeople = config.people ?? true;
  const allowGroups = config.groups ?? true;
  const multiple = schema.type === 'array';
  const selected = (Array.isArray(formData) ? formData : typeof formData === 'string' ? [formData] : []) as string[];
  const people = useQuery({ ...assignablePeopleQuery(config.memberOf), enabled: allowPeople });
  const groups = useQuery({ ...groupsQuery, enabled: allowGroups });
  // Selected users that are not choices (e.g. disabled since, or a default) still show their names.
  const names = useQuery(usersByIdQuery(selected));
  const personOption = (user: UserResponse): ComboboxOption => ({
    value: user.id!,
    label: userName(user, user.id!),
    hint: user.userName ?? undefined,
    icon: <UserRound className="size-3.5" />,
  });
  const options: ComboboxOption[] = [
    ...(allowPeople ? (people.data ?? []).filter((user) => user.id).map(personOption) : []),
    ...(allowGroups
      ? (groups.data ?? [])
          .filter((group) => group.id)
          .map((group) => ({
            value: group.id!,
            label: group.name ?? group.id!,
            hint: 'Group',
            icon: <Users className="size-3.5" />,
          }))
      : []),
  ];
  const known = new Map([
    ...(names.data ?? []).filter((user) => user.id).map((user) => [user.id!, personOption(user)] as const),
    ...options.map((option) => [option.value, option] as const),
  ]);
  const id = fieldPathId.$id;
  return (
    <div className="flex flex-col gap-2">
      <Label id={`${id}-label`}>
        {schema.title ?? fieldPathId.path.at(-1)}
        {required ? ' *' : ''}
      </Label>
      {schema.description && <p className="text-sm text-muted">{schema.description}</p>}
      <Combobox
        id={id}
        aria-labelledby={`${id}-label`}
        multiple={multiple}
        disabled={disabled || readonly}
        loading={people.isFetching || groups.isFetching}
        aria-invalid={!!rawErrors?.length}
        placeholder="Choose people or groups…"
        selected={selected.map(
          (value) =>
            known.get(value) ?? { value, label: names.isLoading || groups.isLoading ? '…' : 'Unknown person or group' },
        )}
        options={options}
        onChange={(values) =>
          onChange(multiple ? values.map((option) => option.value) : values[0]?.value, fieldPathId.path)
        }
      />
      {(people.isError || groups.isError || names.isError) && <Alert>Could not load choices. Try again.</Alert>}
      {rawErrors?.map((error) => (
        <p key={error} className="text-sm text-danger">
          {error}
        </p>
      ))}
    </div>
  );
}
