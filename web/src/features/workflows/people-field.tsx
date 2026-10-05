import type { FieldProps } from '@rjsf/utils';
import { useQuery } from '@tanstack/react-query';
import { UserRound, Users } from 'lucide-react';
import { useMemo } from 'react';
import { api } from '@/api/client';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { Alert } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { groupsQuery, userName, usersQuery } from '@/features/fields/directory';

interface PeopleOptions {
  kind: 'people';
  groupId?: string;
  people?: boolean;
  groups?: boolean;
}

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
  const multiple = schema.type === 'array';
  const selected = (Array.isArray(formData) ? formData : typeof formData === 'string' ? [formData] : []) as string[];
  const users = useQuery(usersQuery);
  const groups = useQuery(groupsQuery);
  const groupMembers = useQuery({
    queryKey: ['workflowPeopleGroupMembers', config.groupId],
    queryFn: async () => {
      const members = new Set<string>();
      const visited = new Set<string>();
      const visit = async (groupId: string) => {
        if (visited.has(groupId)) return;
        visited.add(groupId);
        const [people, nested] = await Promise.all([
          api.v10.groups.byId(groupId).members.get(),
          api.v10.groups.byId(groupId).groups.get(),
        ]);
        for (const person of people ?? []) if (person.id) members.add(person.id);
        await Promise.all((nested ?? []).filter((group) => group.id).map((group) => visit(group.id!)));
      };
      await visit(config.groupId!);
      return [...members];
    },
    enabled: !!config.groupId && (config.people ?? true),
    staleTime: 5 * 60_000,
  });
  const allowedMembers = useMemo(() => new Set(groupMembers.data ?? []), [groupMembers.data]);
  const options: ComboboxOption[] = [
    ...((config.people ?? true)
      ? (users.data ?? [])
          .filter((user) => !user.isDisabled && user.id && (!config.groupId || allowedMembers.has(user.id)))
          .map((user) => ({
            value: user.id!,
            label: userName(user, user.id!),
            hint: user.userName ?? undefined,
            icon: <UserRound className="size-3.5" />,
          }))
      : []),
    ...((config.groups ?? true)
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
  const known = new Map(options.map((option) => [option.value, option]));
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
        loading={users.isFetching || groups.isFetching || groupMembers.isFetching}
        aria-invalid={!!rawErrors?.length}
        placeholder="Choose people or groups…"
        selected={selected.map((value) => known.get(value) ?? { value, label: value })}
        options={options}
        onChange={(values) =>
          onChange(multiple ? values.map((option) => option.value) : values[0]?.value, fieldPathId.path)
        }
      />
      {(users.isError || groups.isError || groupMembers.isError) && <Alert>Could not load choices. Try again.</Alert>}
      {rawErrors?.map((error) => (
        <p key={error} className="text-sm text-danger">
          {error}
        </p>
      ))}
    </div>
  );
}
