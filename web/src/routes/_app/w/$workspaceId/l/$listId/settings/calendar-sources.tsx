import type { CalendarSourceResponse, CalendarSourceUpdate } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { calendarSourcesQuery } from '@/features/calendar/source-queries';
import { useCanManageList } from '@/features/list-settings/queries';
import { listBuilder } from '@/features/lists/queries';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/calendar-sources')({
  component: CalendarSources,
});

function CalendarSources() {
  const { workspaceId, listId } = Route.useParams();
  const queryClient = useQueryClient();
  const canManage = useCanManageList(workspaceId, listId);
  const { data: sources, error } = useQuery(calendarSourcesQuery(workspaceId, listId));
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const add = useMutation({
    mutationFn: () => listBuilder(workspaceId, listId).calendarSources.post({ name, url }),
    onSuccess: async () => {
      setName('');
      setUrl('');
      toast.success('Calendar source added. Its first refresh is queued.');
      await queryClient.invalidateQueries({ queryKey: keys.calendarSources(workspaceId, listId) });
    },
  });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate();
  };
  return (
    <div className="flex flex-col gap-6">
      <SettingsSection
        title="Calendar sources"
        description="Events from these iCalendar URLs refresh every 15 minutes. Edit imported calendar fields in their source calendar."
      >
        <p className="mb-4 text-sm text-muted">
          Add multiple feeds to this list. The same URL can also be used in other lists independently. Google Calendar's
          secret iCal address works here; no Google API setup is needed.
        </p>
        {error && <Alert>{problemMessage(error)}</Alert>}
        {!sources && !error && <Skeleton className="h-24" />}
        {sources?.length === 0 && <p className="text-sm text-muted">No calendar sources yet.</p>}
        <ul className="space-y-4">
          {sources?.map((source) => (
            <SourceCard
              key={source.id}
              source={source}
              workspaceId={workspaceId}
              listId={listId}
              canManage={canManage}
            />
          ))}
        </ul>
      </SettingsSection>
      <SettingsSection
        title="Add a source"
        description="Imported events are visible to people who can read this list. Keep secret feed URLs private."
      >
        <form onSubmit={submit} className="space-y-3">
          <fieldset disabled={!canManage || add.isPending} className="space-y-3">
            <div>
              <Label htmlFor="calendar-source-name">Name</Label>
              <Input
                id="calendar-source-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                maxLength={200}
              />
            </div>
            <div>
              <Label htmlFor="calendar-source-url">iCalendar URL</Label>
              <Input
                id="calendar-source-url"
                type="password"
                autoComplete="off"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                required
                placeholder="https://…/calendar.ics"
              />
            </div>
            <Button type="submit" variant="primary" disabled={!name.trim() || !url.trim()}>
              Add source
            </Button>
          </fieldset>
          {add.isError && <Alert>{problemMessage(add.error)}</Alert>}
        </form>
      </SettingsSection>
    </div>
  );
}

function SourceCard({
  source,
  workspaceId,
  listId,
  canManage,
}: {
  source: CalendarSourceResponse;
  workspaceId: string;
  listId: string;
  canManage: boolean;
}) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const [replacing, setReplacing] = useState(false);
  const [url, setUrl] = useState('');
  const [reset, setReset] = useState(false);
  const builder = listBuilder(workspaceId, listId).calendarSources.bySourceId(source.id!);
  const invalidate = async () => {
    await queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) });
  };
  const change = useMutation({
    mutationFn: (request: CalendarSourceUpdate) => builder.put(request, ifMatch(source)),
    onSuccess: async () => {
      setReplacing(false);
      setUrl('');
      toast.success('Calendar source updated.');
      await invalidate();
    },
  });
  const refresh = useMutation({
    mutationFn: () => builder.refresh.post(),
    onSuccess: async () => {
      toast.success('Refresh queued.');
      await invalidate();
    },
  });
  const remove = useMutation({
    mutationFn: () => builder.delete(ifMatch(source)),
    onSuccess: async () => {
      toast.success('Source removed. Imported events are kept as local items.');
      await invalidate();
    },
  });
  const busy = source.refreshing || change.isPending || refresh.isPending || remove.isPending;
  const error = change.error ?? refresh.error ?? remove.error;
  return (
    <li className="rounded-md border p-4">
      <h3 className="font-medium">{source.name}</h3>
      <p className="mt-1 text-xs text-muted">
        {source.paused ? 'Paused' : source.refreshing ? 'Refreshing…' : 'Active'} · Last success:{' '}
        {source.lastSuccess ? format.dateTime(source.lastSuccess) : 'Not yet'}
      </p>
      <p className="mt-1 text-xs text-muted">
        Last refresh: {source.created ?? 0} created, {source.updated ?? 0} updated, {source.removed ?? 0} removed.
      </p>
      {source.errorEscaped && <Alert className="mt-2">{source.errorEscaped}</Alert>}
      <div className="mt-3 flex flex-wrap gap-2">
        <Button size="sm" disabled={!canManage || busy || !!source.paused} onClick={() => refresh.mutate()}>
          Refresh now
        </Button>
        <Button size="sm" disabled={!canManage || busy} onClick={() => change.mutate({ paused: !source.paused })}>
          {source.paused ? 'Resume' : 'Pause'}
        </Button>
        <Button size="sm" disabled={!canManage || busy} onClick={() => setReplacing(!replacing)}>
          Replace URL
        </Button>
        <Button size="sm" variant="danger" disabled={!canManage || busy} onClick={() => remove.mutate()}>
          Remove source
        </Button>
      </div>
      {replacing && (
        <form
          className="mt-3 space-y-2"
          onSubmit={(event) => {
            event.preventDefault();
            change.mutate({ url, reset });
          }}
        >
          <Label htmlFor={`source-url-${source.id}`}>Replacement iCalendar URL</Label>
          <Input
            id={`source-url-${source.id}`}
            type="password"
            autoComplete="off"
            required
            value={url}
            onChange={(event) => setUrl(event.target.value)}
          />
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={reset} onChange={(event) => setReset(event.target.checked)} />
            This is a different feed; keep previous events as local items.
          </label>
          <Button type="submit" size="sm" disabled={busy || !url.trim()}>
            Save URL
          </Button>
        </form>
      )}
      {error && <Alert className="mt-2">{problemMessage(error)}</Alert>}
    </li>
  );
}
