import type { SettingsResponse, SubscriptionResponse } from '@paperdotnet/client';
import { fieldsOf, ifMatch, TimeOnly } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link } from '@tanstack/react-router';
import { Bell, BellOff, RefreshCw, Send } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { listsQuery, subscriptionsQuery } from '@/api/queries';
import { Button } from '@/components/ui/button';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { itemLink } from '@/features/lists/item-link';
import { itemQuery } from '@/features/lists/queries';
import { CopyField, SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/settings/notifications')({ component: NotificationSettings });

/** The notification types the app sends; types from extensions appear with their key once configured. */
const types: { key: string; label: string; description: string }[] = [
  { key: 'mention', label: 'Mentions', description: 'Someone @mentions you in a comment.' },
  { key: 'itemChanged', label: 'Followed items', description: 'Something you follow changes.' },
  { key: 'digest', label: 'Digests', description: 'Daily summaries of what you follow.' },
  { key: 'reminder', label: 'Reminders', description: 'Tasks that are due and events that start soon.' },
  { key: 'automation', label: 'Automations and approvals', description: 'Approvals waiting for you, run results.' },
  { key: 'system', label: 'System', description: 'Test notifications and messages from the system.' },
];

interface Choice {
  inApp: boolean;
  webhook: boolean;
}
interface Form {
  channels: Record<string, Choice>;
  webhookUrl: string;
  quiet: boolean;
  quietStart: string;
  quietEnd: string;
  digestHour: number;
}

const settingsQuery = {
  queryKey: ['me', 'notificationSettings'],
  queryFn: async () => (await api.v10.me.notificationSettings.get())!,
};

const pad = (n: number) => String(n).padStart(2, '0');
const timeText = (t: TimeOnly | null | undefined) => (t ? `${pad(t.hours)}:${pad(t.minutes)}` : '');
const timeOf = (text: string) => {
  const [hours, minutes] = text.split(':').map(Number);
  return new TimeOnly({ hours, minutes });
};

function formOf(settings: SettingsResponse): Form {
  const stored = (settings.channels?.additionalData ?? {}) as Record<string, Partial<Choice> | undefined>;
  const keys = new Set([...types.map((t) => t.key), ...Object.keys(stored)]);
  // Types without a choice go to every channel (the server's default).
  const channels = Object.fromEntries(
    [...keys].map((key) => [key, { inApp: stored[key]?.inApp ?? true, webhook: stored[key]?.webhook ?? true }]),
  );
  return {
    channels,
    webhookUrl: settings.webhookUrl ?? '',
    quiet: !!settings.quietHoursStart,
    quietStart: timeText(settings.quietHoursStart) || '22:00',
    quietEnd: timeText(settings.quietHoursEnd) || '07:00',
    digestHour: settings.digestHour ?? 7,
  };
}

function NotificationSettings() {
  return (
    <>
      <Delivery />
      <Following />
    </>
  );
}

/** Channels per type, the webhook, quiet hours and the digest hour (NTF-04, NTF-05). */
function Delivery() {
  const queryClient = useQueryClient();
  const { data: settings } = useQuery(settingsQuery);
  const [form, setForm] = useState<Form>();
  const [baseline, setBaseline] = useState<string>();
  const [secret, setSecret] = useState<string>();
  const etag = settings?.odataEtag ?? undefined;
  if (settings && etag !== baseline) {
    setBaseline(etag);
    setForm(formOf(settings));
  }

  const saved = (updated: SettingsResponse | undefined) => {
    if (!updated) return;
    queryClient.setQueryData(settingsQuery.queryKey, updated);
    setSecret(updated.webhookSecret ?? undefined);
  };
  const save = useMutation({
    meta: { silent: true },
    mutationFn: (value: Form) =>
      api.v10.me.notificationSettings.put(
        {
          channels: { additionalData: value.channels },
          webhookUrl: value.webhookUrl.trim() || null,
          quietHoursStart: value.quiet ? timeOf(value.quietStart) : null,
          quietHoursEnd: value.quiet ? timeOf(value.quietEnd) : null,
          digestHour: value.digestHour,
        },
        ifMatch(settings),
      ),
    onSuccess: (updated) => {
      saved(updated);
      toast.success('Notification settings saved.');
    },
  });
  const rotate = useMutation({
    mutationFn: () => api.v10.me.notificationSettings.webhookSecret.post(),
    onSuccess: saved,
  });
  const test = useMutation({
    mutationFn: () => api.v10.me.notificationSettings.test.post(),
    onSuccess: () => toast.success('Test notification sent. It arrives in a moment.'),
  });

  if (!settings || !form) return <Skeleton className="h-96" />;
  const initial = formOf(settings);
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const hasWebhook = !!settings.webhookUrl;
  const setChoice = (key: string, channel: keyof Choice, value: boolean) =>
    setForm({ ...form, channels: { ...form.channels, [key]: { ...form.channels[key]!, [channel]: value } } });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate(form);
  };

  return (
    <form onSubmit={onSubmit}>
      <SettingsSection
        title="Notifications"
        description="What reaches you, and where. Everything also shows under Notifications."
        actions={
          <>
            <Button type="button" className="mr-auto" disabled={test.isPending} onClick={() => test.mutate()}>
              <Send /> Send a test
            </Button>
            {dirty && <span className="text-xs text-muted">Unsaved changes</span>}
            <Button type="submit" variant="primary" disabled={!dirty || save.isPending}>
              Save
            </Button>
          </>
        }
      >
        <div className="-mx-5 overflow-x-auto">
          <table className="w-full text-[13px]">
            <thead>
              <tr className="border-y bg-surface-muted/40 text-left text-xs text-muted">
                <th className="px-5 py-2 font-medium">Notification</th>
                <th className="w-20 px-2 py-2 text-center font-medium">In app</th>
                <th className="w-20 px-5 py-2 text-center font-medium">Webhook</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {Object.keys(form.channels).map((key) => {
                const type = types.find((t) => t.key === key);
                const label = type?.label ?? key;
                return (
                  <tr key={key}>
                    <td className="px-5 py-2.5">
                      <p className="font-medium">{label}</p>
                      {type && <p className="text-xs text-muted">{type.description}</p>}
                    </td>
                    <td className="px-2 text-center">
                      <Checkbox
                        aria-label={`${label} in app`}
                        checked={form.channels[key]!.inApp}
                        onChange={(e) => setChoice(key, 'inApp', e.target.checked)}
                      />
                    </td>
                    <td className="px-5 text-center">
                      <Checkbox
                        aria-label={`${label} by webhook`}
                        checked={form.channels[key]!.webhook}
                        disabled={!form.webhookUrl.trim()}
                        onChange={(e) => setChoice(key, 'webhook', e.target.checked)}
                      />
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        <div className="mt-4 divide-y">
          <SettingRow
            id="webhook-url"
            label="Webhook"
            hint="Notifications are posted as JSON to this https address, signed with the secret (header X-PaperDotNet-Signature). Use it for ntfy, Gotify, chat bots or your own automation."
          >
            <div className="flex gap-2">
              <Input
                id="webhook-url"
                type="url"
                placeholder="https://…"
                value={form.webhookUrl}
                onChange={(e) => setForm({ ...form, webhookUrl: e.target.value })}
              />
              {hasWebhook && (
                <Button type="button" disabled={rotate.isPending} onClick={() => rotate.mutate()}>
                  <RefreshCw /> New secret
                </Button>
              )}
            </div>
            {secret && (
              <Alert tone="success" className="mt-1 flex flex-col gap-2">
                <p>Copy the signing secret now: it is shown only once. The previous secret no longer works.</p>
                <CopyField label="Webhook secret" value={secret} />
              </Alert>
            )}
          </SettingRow>
          <SettingRow
            id="quiet-hours"
            label="Quiet hours"
            hint="Webhooks wait until the quiet hours end (in your time zone); in-app notifications still arrive."
          >
            <div className="flex flex-wrap items-center gap-2">
              <Checkbox
                id="quiet-hours"
                checked={form.quiet}
                onChange={(e) => setForm({ ...form, quiet: e.target.checked })}
              />
              <Input
                aria-label="Quiet from"
                type="time"
                className="w-32"
                disabled={!form.quiet}
                value={form.quietStart}
                onChange={(e) => setForm({ ...form, quietStart: e.target.value })}
              />
              <span className="text-muted">to</span>
              <Input
                aria-label="Quiet until"
                type="time"
                className="w-32"
                disabled={!form.quiet}
                value={form.quietEnd}
                onChange={(e) => setForm({ ...form, quietEnd: e.target.value })}
              />
            </div>
          </SettingRow>
          <SettingRow id="digest-hour" label="Daily digest at" hint="For what you follow with a daily digest.">
            <Select
              id="digest-hour"
              className="w-32"
              value={form.digestHour}
              onChange={(e) => setForm({ ...form, digestHour: Number(e.target.value) })}
            >
              {Array.from({ length: 24 }, (_, hour) => (
                <option key={hour} value={hour}>
                  {pad(hour)}:00
                </option>
              ))}
            </Select>
          </SettingRow>
        </div>
        {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
      </SettingsSection>
    </form>
  );
}

/** Lists and items the user follows (NTF-03), with how often, and unfollowing. */
function Following() {
  const { data, isPending } = useQuery(subscriptionsQuery);
  return (
    <SettingsSection
      title="Following"
      description="Follow an item or a list from its menu to be notified when it changes."
      className="px-0 pb-0"
    >
      {isPending ? (
        <Skeleton className="mx-5 mb-5 h-12" />
      ) : data?.length ? (
        <ul className="divide-y border-t">
          {data.map((subscription) => (
            <FollowRow key={subscription.id} subscription={subscription} />
          ))}
        </ul>
      ) : (
        <EmptyState icon={BellOff} title="You follow nothing yet" className="border-t py-6" />
      )}
    </SettingsSection>
  );
}

function FollowRow({ subscription: s }: { subscription: SubscriptionResponse }) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const { data: lists } = useQuery(listsQuery(s.workspaceId!));
  const item = useQuery({ ...itemQuery(s.workspaceId!, s.listId!, s.itemId ?? ''), enabled: !!s.itemId });
  const list = lists?.find((l) => l.id === s.listId);
  const title = s.itemId ? String(item.data ? (fieldsOf(item.data).title ?? 'Untitled') : '…') : (list?.name ?? '…');
  const invalidate = () => queryClient.invalidateQueries({ queryKey: subscriptionsQuery.queryKey });
  const change = useMutation({
    mutationFn: (frequency: 'immediate' | 'daily') =>
      api.v10.me.subscriptions.post({ workspaceId: s.workspaceId, listId: s.listId, itemId: s.itemId, frequency }),
    onSuccess: invalidate,
  });
  const unfollow = useMutation({
    mutationFn: () => api.v10.me.subscriptions.byId(s.id!).delete(),
    onSuccess: async () => {
      toast.success(`You no longer follow “${title}”.`);
      await invalidate();
    },
  });
  const link = s.itemId
    ? itemLink({ workspaceId: s.workspaceId, listId: s.listId, itemId: s.itemId })
    : { to: '/w/$workspaceId/l/$listId' as const, params: { workspaceId: s.workspaceId!, listId: s.listId! } };

  return (
    <li className="flex flex-wrap items-center gap-3 px-5 py-3">
      <Bell className="size-4 text-muted" />
      <div className="min-w-0 flex-1">
        {link ? (
          <Link {...link} className="block truncate text-[13px] font-medium hover:underline">
            {title}
          </Link>
        ) : (
          <span className="block truncate text-[13px] font-medium">{title}</span>
        )}
        <p className="text-xs text-muted">
          {s.itemId ? `Item in ${list?.name ?? '…'}` : 'Whole list'} · since {format.date(s.createdAt)}
        </p>
      </div>
      <Select
        aria-label={`How often for ${title}`}
        className="w-40"
        value={s.frequency ?? 'immediate'}
        disabled={change.isPending}
        onChange={(e) => change.mutate(e.target.value as 'immediate' | 'daily')}
      >
        <option value="immediate">Immediately</option>
        <option value="daily">Daily digest</option>
      </Select>
      <Button size="sm" disabled={unfollow.isPending} onClick={() => unfollow.mutate()}>
        Unfollow
      </Button>
    </li>
  );
}
