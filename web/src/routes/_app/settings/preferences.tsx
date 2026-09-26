import type { PreferencesPatch, PreferencesResponse } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { preferencesQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { cultureName, cultures, dateFormats, timeZones, withValue } from '@/features/settings/preference-options';
import { SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { createFormatter } from '@/lib/format';
import { applyTheme, type Theme } from '@/lib/preferences';

export const Route = createFileRoute('/_app/settings/preferences')({ component: Preferences });

const names = [
  'language',
  'timeZone',
  'dateFormat',
  'timeFormat',
  'numberFormat',
  'theme',
  'documentLanguages',
] as const;
type Name = (typeof names)[number];
/** A value per preference; '' means "use the organization's default". */
type Values = Record<Name, string>;

const zones = timeZones();

const organizationPreferencesQuery = {
  queryKey: ['organization', 'preferences'],
  queryFn: async () => (await api.v10.organization.preferences.get())!,
  staleTime: 5 * 60_000,
};

function own(preferences: PreferencesResponse): Values {
  const inherited = new Set(preferences.inherited ?? []);
  return Object.fromEntries(
    names.map((name) => [name, inherited.has(name) ? '' : ((preferences[name] as string | null | undefined) ?? '')]),
  ) as Values;
}

/** Language, time zone, formats, theme and document languages (PLT-17), each either set or the organization's. */
function Preferences() {
  const queryClient = useQueryClient();
  const { data: preferences } = useQuery(preferencesQuery);
  const { data: defaults } = useQuery(organizationPreferencesQuery);
  const [values, setValues] = useState<Values>();
  const [baseline, setBaseline] = useState<string>();
  // A new version from the server (saved here, in another tab or by the theme menu) reloads the form.
  const etag = preferences?.odataEtag ?? undefined;
  if (preferences && etag !== baseline) {
    setBaseline(etag);
    setValues(own(preferences));
  }
  const save = useMutation({
    mutationFn: async (patch: PreferencesPatch) => (await api.v10.me.preferences.patch(patch, ifMatch(preferences)))!,
    onSuccess: (updated) => {
      queryClient.setQueryData(keys.preferences, updated);
      if (updated.theme) applyTheme(updated.theme as Theme);
      toast.success('Preferences saved.');
    },
  });

  if (!preferences || !values) return <Skeleton className="h-96" />;
  const saved = own(preferences);
  const changed = names.filter((name) => values[name] !== saved[name]);
  const set = (name: Name) => (value: string) => setValues({ ...values, [name]: value });
  const defaultOf = (name: Name) => (defaults?.[name] as string | null | undefined) ?? undefined;
  // The effective values, to show what dates and numbers will look like.
  const effective = (name: Name) => values[name] || defaultOf(name) || '';
  const sample = createFormatter({
    language: effective('language') || undefined,
    timeZone: effective('timeZone') || undefined,
    dateFormat: effective('dateFormat') || undefined,
    timeFormat: effective('timeFormat') === '12h' ? '12h' : '24h',
    numberFormat: effective('numberFormat') || undefined,
  });
  const now = new Date();

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    // A JSON merge patch: null goes back to the organization's default.
    const patch = Object.fromEntries(changed.map((name) => [name, values[name] || null])) as PreferencesPatch;
    save.mutate(patch);
  };

  const defaultOption = (name: Name, label?: (value: string) => string) => {
    const value = defaultOf(name);
    return <option value="">Organization default{value ? ` (${label ? label(value) : value})` : ''}</option>;
  };
  const language = effective('language') || 'en';

  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Preferences"
        description="How dates, times and numbers look for you, everywhere in the app and in notifications."
        actions={
          <>
            {changed.length > 0 && <span className="mr-auto text-xs text-muted">Unsaved changes</span>}
            <Button type="button" disabled={!changed.length} onClick={() => setValues(saved)}>
              Reset
            </Button>
            <Button type="submit" variant="primary" disabled={!changed.length || save.isPending}>
              Save
            </Button>
          </>
        }
      >
        <div className="divide-y">
          <SettingRow id="pref-language" label="Language">
            <Select id="pref-language" value={values.language} onChange={(e) => set('language')(e.target.value)}>
              {defaultOption('language', (v) => cultureName(v, language))}
              {withValue(cultures, values.language, saved.language).map((c) => (
                <option key={c} value={c}>
                  {cultureName(c, language)}
                </option>
              ))}
            </Select>
          </SettingRow>
          <SettingRow
            id="pref-timeZone"
            label="Time zone"
            hint={`Now: ${sample.time(now)} · ${Intl.DateTimeFormat().resolvedOptions().timeZone} on this device`}
          >
            <Select id="pref-timeZone" value={values.timeZone} onChange={(e) => set('timeZone')(e.target.value)}>
              {defaultOption('timeZone')}
              {withValue(zones, values.timeZone, saved.timeZone).map((z) => (
                <option key={z} value={z}>
                  {z.replaceAll('_', ' ')}
                </option>
              ))}
            </Select>
          </SettingRow>
          <SettingRow id="pref-dateFormat" label="Date format" hint={`Today: ${sample.date(now)}`}>
            <Select id="pref-dateFormat" value={values.dateFormat} onChange={(e) => set('dateFormat')(e.target.value)}>
              {defaultOption('dateFormat')}
              {withValue(dateFormats, values.dateFormat, saved.dateFormat).map((f) => (
                <option key={f} value={f}>
                  {f} — {createFormatter({ dateFormat: f, timeZone: sample.preferences.timeZone }).date(now)}
                </option>
              ))}
            </Select>
          </SettingRow>
          <SettingRow id="pref-timeFormat" label="Time format">
            <Select id="pref-timeFormat" value={values.timeFormat} onChange={(e) => set('timeFormat')(e.target.value)}>
              {defaultOption('timeFormat')}
              <option value="24h">24-hour (14:30)</option>
              <option value="12h">12-hour (2:30 PM)</option>
            </Select>
          </SettingRow>
          <SettingRow id="pref-numberFormat" label="Number format" hint={`Example: ${sample.number(1234567.89)}`}>
            <Select
              id="pref-numberFormat"
              value={values.numberFormat}
              onChange={(e) => set('numberFormat')(e.target.value)}
            >
              {defaultOption('numberFormat', (v) => cultureName(v, language))}
              {withValue(cultures, values.numberFormat, saved.numberFormat).map((c) => (
                <option key={c} value={c}>
                  {cultureName(c, language)} — {createFormatter({ numberFormat: c }).number(1234.5)}
                </option>
              ))}
            </Select>
          </SettingRow>
          <SettingRow id="pref-theme" label="Theme">
            <Select id="pref-theme" value={values.theme} onChange={(e) => set('theme')(e.target.value)}>
              {defaultOption('theme')}
              <option value="system">Same as the device</option>
              <option value="light">Light</option>
              <option value="dark">Dark</option>
            </Select>
          </SettingRow>
          <SettingRow
            id="pref-documentLanguages"
            label="Document languages"
            hint="Languages of the text in your scans, for OCR: Tesseract codes joined with +, e.g. deu+eng. Libraries can set their own."
          >
            <Input
              id="pref-documentLanguages"
              value={values.documentLanguages}
              placeholder={
                defaultOf('documentLanguages') ? `Organization default (${defaultOf('documentLanguages')})` : ''
              }
              onChange={(e) => set('documentLanguages')(e.target.value.trim())}
              pattern="[a-z_]{3,}(\+[a-z_]{3,})*"
            />
          </SettingRow>
        </div>
        {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}
