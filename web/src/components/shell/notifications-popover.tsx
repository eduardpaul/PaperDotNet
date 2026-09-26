import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Bell, CheckCheck } from 'lucide-react';
import { useState } from 'react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { notificationsQuery, unreadCountQuery } from '@/api/queries';
import { NotificationRow } from '@/features/notifications/notification-row';
import { Button } from '@/components/ui/button';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Popover, PopoverContent, PopoverTrigger, Tooltip } from '@/components/ui/popover';

export function NotificationsPopover() {
  const [open, setOpen] = useState(false);
  const queryClient = useQueryClient();
  const { data: unread } = useQuery(unreadCountQuery);
  const { data, isPending } = useQuery({ ...notificationsQuery, enabled: open });
  const markAll = useMutation({
    mutationFn: () => api.v10.me.notifications.read.post(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.notifications }),
  });

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <Tooltip label="Notifications">
        <PopoverTrigger asChild>
          <Button
            variant="ghost"
            size="icon"
            className="relative"
            aria-label={`Notifications${unread ? ` (${unread} unread)` : ''}`}
          >
            <Bell />
            {!!unread && (
              <span className="absolute top-1.5 right-1.5 size-2 rounded-full bg-accent ring-2 ring-background" />
            )}
          </Button>
        </PopoverTrigger>
      </Tooltip>
      <PopoverContent className="w-[min(92vw,380px)]">
        <div className="flex items-center justify-between border-b px-4 py-2.5">
          <h2 className="text-sm font-semibold">Notifications</h2>
          <Button variant="ghost" size="sm" disabled={!unread || markAll.isPending} onClick={() => markAll.mutate()}>
            <CheckCheck /> Mark all read
          </Button>
        </div>
        <div className="max-h-[60vh] overflow-y-auto">
          {isPending ? (
            <div className="flex flex-col gap-3 p-4">
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
            </div>
          ) : data?.length ? (
            <ul className="divide-y">
              {data.slice(0, 8).map((n) => (
                <NotificationRow key={n.id} notification={n} compact onOpen={() => setOpen(false)} />
              ))}
            </ul>
          ) : (
            <EmptyState icon={Bell} title="You're all caught up" />
          )}
        </div>
        <div className="border-t p-1.5">
          <Button asChild variant="ghost" size="sm" className="w-full">
            <Link to="/notifications" onClick={() => setOpen(false)}>
              See all notifications
            </Link>
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
