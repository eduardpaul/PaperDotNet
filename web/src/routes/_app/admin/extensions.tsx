import type { ExtensionResponse } from '@paperdotnet/client';
import { fields as jsonObject, fieldsOf, jsonOf } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { Puzzle } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/admin/extensions')({ component: Extensions });

const extensionsQuery = { queryKey: ['extensions'], queryFn: async () => (await api.v10.extensions.get()) ?? [] };

/** Extensions compiled into this installation (EXT-03): turn them on per organization and set them up. */
function Extensions() {
  const { data, isPending } = useQuery(extensionsQuery);
  if (isPending) return <Skeleton className="h-48" />;
  if (!data?.length)
    return (
      <SettingsSection title="Extensions">
        <EmptyState icon={Puzzle} title="No extensions installed" />
      </SettingsSection>
    );
  return (
    <>
      {data.map((extension) => (
        <ExtensionCard key={extension.id} extension={extension} />
      ))}
    </>
  );
}

function ExtensionCard({ extension }: { extension: ExtensionResponse }) {
  const queryClient = useQueryClient();
  const settingsKey = ['extensions', extension.id, 'settings'];
  const { data: settings } = useQuery({
    queryKey: settingsKey,
    queryFn: async () => ({ ...fieldsOf({ fields: await api.v10.extensions.byId(extension.id!).settings.get() }) }),
    enabled: !!extension.settings?.length,
  });
  const [values, setValues] = useState<Record<string, unknown>>();
  const [baseline, setBaseline] = useState<Record<string, unknown>>();
  if (settings && settings !== baseline) {
    setBaseline(settings);
    setValues(settings);
  }
  const toggle = useMutation({
    mutationFn: () =>
      extension.enabled
        ? api.v10.extensions.byId(extension.id!).disable.post()
        : api.v10.extensions.byId(extension.id!).enable.post(),
    onSuccess: async (updated) => {
      toast.success(`${extension.name} ${updated?.enabled ? 'turned on' : 'turned off'}.`);
      await queryClient.invalidateQueries({ queryKey: extensionsQuery.queryKey });
    },
  });
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => api.v10.extensions.byId(extension.id!).settings.put(jsonObject(values ?? {})),
    onSuccess: async () => {
      toast.success('Settings saved.');
      await queryClient.invalidateQueries({ queryKey: settingsKey });
    },
  });
  const contributions = Object.entries(extension.contributions ?? {})
    .filter(([key, value]) => key !== 'additionalData' && Array.isArray(value) && value.length)
    .map(([key, value]) => `${(value as unknown[]).length} ${key.replace(/([A-Z])/g, ' $1').toLowerCase()}`);
  return (
    <SettingsSection
      title={extension.name ?? extension.id!}
      description={
        <>
          {extension.description}{' '}
          <span className="text-xs">
            v{extension.version}
            {extension.publisher && ` · ${extension.publisher}`}
          </span>
        </>
      }
      actions={
        <>
          <span className="mr-auto">{extension.enabled ? <Badge tone="success">On</Badge> : <Badge>Off</Badge>}</span>
          {!!extension.settings?.length && (
            <Button disabled={!extension.enabled || save.isPending} onClick={() => save.mutate()}>
              Save settings
            </Button>
          )}
          <Button
            variant={extension.enabled ? 'secondary' : 'primary'}
            disabled={toggle.isPending}
            onClick={() => toggle.mutate()}
          >
            {extension.enabled ? 'Turn off' : 'Turn on'}
          </Button>
        </>
      }
    >
      {contributions.length > 0 && <p className="text-xs text-muted">Adds {contributions.join(', ')}.</p>}
      {!!extension.scopes?.length && (
        <p className="mt-1 text-xs text-muted">Scopes: {extension.scopes.map((s) => s.name).join(', ')}</p>
      )}
      {!!extension.settings?.length && values && (
        <fieldset disabled={!extension.enabled} className="mt-3 divide-y border-t">
          {extension.settings.map((setting) => {
            const id = `ext-${extension.id}-${setting.name}`;
            const value = values[setting.name!] ?? jsonOf(setting.defaultEscaped);
            const set = (v: unknown) => setValues({ ...values, [setting.name!]: v });
            return (
              <SettingRow key={setting.name} id={id} label={setting.name} hint={setting.description}>
                {setting.type === 'boolean' ? (
                  <label className="flex items-center gap-2 pt-2 text-[13px]">
                    <Checkbox id={id} checked={!!value} onChange={(e) => set(e.target.checked)} /> On
                  </label>
                ) : setting.type === 'choice' ? (
                  <Select id={id} value={String(value ?? '')} onChange={(e) => set(e.target.value)}>
                    {!setting.required && <option value="">—</option>}
                    {setting.choices?.map((c) => (
                      <option key={c} value={c}>
                        {c}
                      </option>
                    ))}
                  </Select>
                ) : (
                  <Input
                    id={id}
                    type={setting.type === 'number' ? 'number' : 'text'}
                    required={!!setting.required}
                    value={String(value ?? '')}
                    onChange={(e) => set(setting.type === 'number' ? Number(e.target.value) : e.target.value)}
                  />
                )}
              </SettingRow>
            );
          })}
        </fieldset>
      )}
      {(toggle.isError || save.isError) && <Alert className="mt-3">{problemMessage(toggle.error ?? save.error)}</Alert>}
    </SettingsSection>
  );
}
