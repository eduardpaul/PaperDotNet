import type { PreferencesPatch } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { cultures, dateFormats, timeZones } from '@/features/settings/preference-options';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/admin/organization')({ component: Organization });

const defaultsQuery = {
  queryKey: ['organization', 'preferences'],
  queryFn: async () => (await api.v10.organization.preferences.get())!,
};

function Organization() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(defaultsQuery);
  const [values, setValues] = useState<PreferencesPatch>();
  const current = values ?? {
    language: data?.language ?? 'en',
    timeZone: data?.timeZone ?? 'UTC',
    dateFormat: data?.dateFormat ?? 'yyyy-MM-dd',
    timeFormat: data?.timeFormat ?? '24h',
    numberFormat: data?.numberFormat ?? 'en',
    theme: data?.theme ?? 'system',
    documentLanguages: data?.documentLanguages ?? 'eng',
  };
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.organization.preferences.patch(current, ifMatch(data)),
    onSuccess: async (saved) => {
      setValues(undefined);
      toast.success('Organization defaults saved.');
      queryClient.setQueryData(defaultsQuery.queryKey, saved);
    },
  });

  if (isPending || !data) return <Skeleton className="h-64" />;

  return (
    <form
      onSubmit={(event: FormEvent) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <SettingsSection
        title="Defaults"
        description="New people and libraries start from these. A person can override them in their own preferences."
        actions={
          <Button type="submit" variant="primary" disabled={!values || save.isPending}>
            Save defaults
          </Button>
        }
      >
        {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
        <div className="grid gap-3 sm:grid-cols-2">
          <Choice
            label="Language"
            value={String(current.language ?? '')}
            options={cultures}
            onChange={(language) => setValues({ ...current, language })}
          />
          <Choice
            label="Time zone"
            value={String(current.timeZone ?? '')}
            options={withCurrent(timeZones(), String(current.timeZone ?? ''))}
            onChange={(timeZone) => setValues({ ...current, timeZone })}
          />
          <Choice
            label="Date format"
            value={String(current.dateFormat ?? '')}
            options={withCurrent(dateFormats, String(current.dateFormat ?? ''))}
            onChange={(dateFormat) => setValues({ ...current, dateFormat })}
          />
          <Choice
            label="Time format"
            value={String(current.timeFormat ?? '24h')}
            options={['24h', '12h']}
            onChange={(timeFormat) => setValues({ ...current, timeFormat: timeFormat === '12h' ? '12h' : '24h' })}
          />
          <Choice
            label="Number format"
            value={String(current.numberFormat ?? '')}
            options={cultures}
            onChange={(numberFormat) => setValues({ ...current, numberFormat })}
          />
          <Choice
            label="Theme"
            value={String(current.theme ?? 'system')}
            options={['system', 'light', 'dark']}
            onChange={(theme) => setValues({ ...current, theme: theme as PreferencesPatch['theme'] })}
          />
          <Choice
            label="OCR languages"
            value={String(current.documentLanguages ?? 'eng')}
            options={withCurrent(['eng', 'deu', 'deu+eng', 'spa', 'fra', 'ita'], String(current.documentLanguages ?? ''))}
            onChange={(documentLanguages) => setValues({ ...current, documentLanguages })}
          />
        </div>
      </SettingsSection>
    </form>
  );
}

function withCurrent(options: string[], value: string) {
  return value && !options.includes(value) ? [value, ...options] : options;
}

function Choice({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: string[];
  onChange: (value: string) => void;
}) {
  const id = label.toLowerCase().replace(/\W+/g, '-');
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        {options.map((option) => (
          <option key={option} value={option}>
            {option}
          </option>
        ))}
      </Select>
    </div>
  );
}
