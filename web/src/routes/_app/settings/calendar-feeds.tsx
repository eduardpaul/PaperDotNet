import type { FeedResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { CalendarSync, Plus, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { useListsByTemplate } from '@/features/lists/lists-by-template';
import { ConfirmDialog, CopyField, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

export const Route = createFileRoute('/_app/settings/calendar-feeds')({ component: CalendarFeeds });

const feedsQuery = {
  queryKey: ['me', 'calendarFeeds'],
  queryFn: async () => (await api.v10.me.calendarFeeds.get()) ?? [],
};

/** Private .ics addresses for other calendar apps (CAL-04): one calendar or task list, or all of the user's. */
function CalendarFeeds() {
  const format = useFormat();
  const queryClient = useQueryClient();
  const calendars = [...useListsByTemplate('calendar'), ...useListsByTemplate('tasks')];
  const { data, isPending } = useQuery(feedsQuery);
  const [creating, setCreating] = useState(false);
  const [removing, setRemoving] = useState<FeedResponse>();
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (feed: FeedResponse) => api.v10.me.calendarFeeds.byId(feed.id!).delete(),
    onSuccess: async () => {
      setRemoving(undefined);
      toast.success('Feed deleted.');
      await queryClient.invalidateQueries({ queryKey: feedsQuery.queryKey });
    },
  });
  const calendarName = (feed: FeedResponse) => {
    if (!feed.listId) return 'Everything in your calendar (events and due tasks)';
    const list = calendars.find((c) => c.id === feed.listId);
    return list ? `${list.workspaceName} › ${list.name}` : 'A calendar';
  };

  return (
    <SettingsSection
      title="Calendar feeds"
      description="Subscribe to your PaperDotNet calendar from Outlook, Google Calendar, Apple Calendar or Thunderbird. Anyone with a feed's address can read it, so keep it private."
      className="px-0 pb-0"
      actions={
        <Button variant="primary" onClick={() => setCreating(true)}>
          <Plus /> New feed
        </Button>
      }
    >
      {isPending ? (
        <Skeleton className="mx-5 mb-5 h-12" />
      ) : data?.length ? (
        <ul className="divide-y border-t">
          {data.map((feed) => (
            <li key={feed.id} className="flex items-center gap-3 px-5 py-3">
              <CalendarSync className="size-4 text-muted" />
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13px] font-medium">{feed.name}</p>
                <p className="truncate text-xs text-muted">
                  {calendarName(feed)} · created {format.date(feed.createdAt)}
                </p>
              </div>
              <Button variant="ghost" size="icon" aria-label={`Delete ${feed.name}`} onClick={() => setRemoving(feed)}>
                <Trash2 />
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState icon={CalendarSync} title="No calendar feeds" className="border-t py-6" />
      )}
      {creating && <NewFeedDialog onClose={() => setCreating(false)} />}
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(open) => !open && setRemoving(undefined)}
        title="Delete this feed?"
        description={`Calendar apps subscribed to “${removing?.name ?? ''}” stop getting updates.`}
        confirm="Delete"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </SettingsSection>
  );
}

function NewFeedDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const events = useListsByTemplate('calendar');
  const tasks = useListsByTemplate('tasks');
  const calendars = [...events, ...tasks];
  const [name, setName] = useState('');
  const [listId, setListId] = useState('');
  const create = useMutation({
    meta: { silent: true },
    mutationFn: async () => {
      const list = calendars.find((c) => c.id === listId);
      return (await api.v10.me.calendarFeeds.post({
        name: name.trim() || list?.name || 'My calendar',
        workspaceId: list?.workspaceId,
        listId: list?.id,
      }))!;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: feedsQuery.queryKey }),
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };
  const url = create.data?.url;

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        {url ? (
          <>
            <DialogHeader>
              <DialogTitle>Feed created</DialogTitle>
            </DialogHeader>
            <div className="flex flex-col gap-3 px-5 pb-4">
              <p className="text-[13px] text-muted">
                Add this address in your calendar app as a subscription (“From URL” or “Subscribe to calendar”). It is
                shown only once.
              </p>
              <CopyField label="Feed address" value={url} />
            </div>
            <DialogFooter>
              <Button variant="primary" onClick={onClose}>
                Done
              </Button>
            </DialogFooter>
          </>
        ) : (
          <form onSubmit={onSubmit}>
            <DialogHeader>
              <DialogTitle>New calendar feed</DialogTitle>
            </DialogHeader>
            <div className="flex flex-col gap-4 px-5 pb-4">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="feed-calendar">Calendar</Label>
                <Select id="feed-calendar" value={listId} onChange={(e) => setListId(e.target.value)}>
                  <option value="">Everything in my calendar (events and due tasks)</option>
                  {[
                    { label: 'Calendars', lists: events },
                    { label: 'Task lists (due dates)', lists: tasks },
                  ].map(
                    (group) =>
                      group.lists.length > 0 && (
                        <optgroup key={group.label} label={group.label}>
                          {group.lists.map((c) => (
                            <option key={c.id} value={c.id!}>
                              {c.workspaceName} › {c.name}
                            </option>
                          ))}
                        </optgroup>
                      ),
                  )}
                </Select>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="feed-name">Name</Label>
                <Input
                  id="feed-name"
                  placeholder="e.g. Phone"
                  maxLength={200}
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              {create.isError && <Alert>{problemMessage(create.error)}</Alert>}
            </div>
            <DialogFooter>
              <Button type="button" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={create.isPending}>
                Create feed
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}
