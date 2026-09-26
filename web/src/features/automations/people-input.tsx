import { useQuery } from '@tanstack/react-query';
import { UserRound, Users } from 'lucide-react';
import { api } from '@/api/client';
import { Combobox, type ComboboxOption } from '@/components/ui/combobox';
import { userName, usersQuery } from '@/features/fields/directory';

const groupsQuery = {
  queryKey: ['groups'],
  queryFn: async () => {
    try {
      return (await api.v10.groups.get({ queryParameters: { top: 200 } }))?.value ?? [];
    } catch {
      return []; // Without the directory scope, groups can still be typed as group:Name.
    }
  },
  staleTime: 5 * 60_000,
};

/**
 * People in automations are names, so automations stay portable (docs/automation.md): user names, group:Name,
 * field:name (a person field of the item), creator and actor. Anything else can be typed.
 */
export function PeopleInput({
  id,
  labelId,
  value,
  onChange,
  personFields,
  disabled,
}: {
  id?: string;
  labelId: string;
  value: string[];
  onChange: (value: string[]) => void;
  /** Person fields of the trigger's list, offered as field:name. */
  personFields: { name: string; label: string }[];
  disabled?: boolean;
}) {
  const { data: users } = useQuery(usersQuery);
  const { data: groups } = useQuery(groupsQuery);
  const options: ComboboxOption[] = [
    { value: 'creator', label: 'The item’s creator', hint: 'creator' },
    { value: 'actor', label: 'Whoever triggered it', hint: 'actor' },
    ...personFields.map((f) => ({
      value: `field:${f.name}`,
      label: `People in “${f.label}”`,
      hint: `field:${f.name}`,
    })),
    ...(groups ?? []).map((g) => ({
      value: `group:${g.name}`,
      label: g.name ?? '',
      hint: 'group',
      icon: <Users className="size-3.5" />,
    })),
    ...(users ?? [])
      .filter((u) => !u.isDisabled && u.userName)
      .map((u) => ({
        value: u.userName!,
        label: userName(u, u.id!),
        hint: u.userName ?? undefined,
        icon: <UserRound className="size-3.5" />,
      })),
  ];
  const selected = value.map((v) => options.find((o) => o.value === v) ?? { value: v, label: v });
  return (
    <Combobox
      id={id}
      aria-labelledby={labelId}
      multiple
      selected={selected}
      options={options}
      placeholder="People, groups or roles…"
      disabled={disabled}
      onChange={(next) => onChange(next.map((o) => o.value))}
      onCreate={(text) => onChange([...value, text.trim()])}
    />
  );
}
