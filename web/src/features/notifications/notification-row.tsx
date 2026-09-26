import type { NotificationResponse } from '@paperdotnet/client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { AtSign, Bell, CalendarClock, CheckCircle2, FileText, MessageSquare, Trash2 } from 'lucide-react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Tooltip } from '@/components/ui/popover';
import { itemLink } from '@/features/lists/item-link';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';

function NotificationIcon({ type }: { type: string | null | undefined }) {
  const className = 'size-3.5';
  if (type?.includes('mention')) return <AtSign className={className} />;
  if (type?.includes('comment')) return <MessageSquare className={className} />;
  if (type?.includes('reminder') || type?.includes('due')) return <CalendarClock className={className} />;
  if (type?.includes('approval')) return <CheckCircle2 className={className} />;
  if (type?.includes('document')) return <FileText className={className} />;
  return <Bell className={className} />;
}

export function NotificationRow({
  notification,
  compact,
  onOpen,
}: {
  notification: NotificationResponse;
  compact?: boolean;
  onOpen?: () => void;
}) {
  const format = useFormat();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const unread = !notification.readAt;
  const invalidate = () => queryClient.invalidateQueries({ queryKey: keys.notifications });
  const markRead = useMutation({
    mutationFn: () => api.v10.me.notifications.byId(notification.id!).read.post(),
    onSuccess: invalidate,
  });
  const remove = useMutation({
    mutationFn: () => api.v10.me.notifications.byId(notification.id!).delete(),
    onSuccess: invalidate,
  });

  return (
    <li className={cn('group relative flex gap-3 px-4 py-3', unread && 'bg-accent-soft/40')}>
      <span
        className={cn(
          'mt-0.5 rounded-full p-1.5',
          unread ? 'bg-accent-soft text-accent' : 'bg-surface-muted text-muted',
        )}
      >
        <NotificationIcon type={notification.type} />
      </span>
      <button
        type="button"
        className="min-w-0 flex-1 text-left"
        onClick={() => {
          if (unread) markRead.mutate();
          onOpen?.();
          const link = itemLink(notification);
          if (link) void navigate(link);
        }}
      >
        <p className={cn('text-[13px]', unread && 'font-medium')}>{notification.title}</p>
        {notification.body && (
          <p className={cn('mt-0.5 text-[13px] text-muted', compact && 'line-clamp-2')}>{notification.body}</p>
        )}
        <p className="mt-1 text-xs text-muted" title={format.dateTime(notification.createdAt)}>
          {format.relative(notification.createdAt)}
        </p>
      </button>
      {!compact && (
        <Tooltip label="Delete">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Delete notification"
            className="opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
            onClick={() => remove.mutate()}
          >
            <Trash2 />
          </Button>
        </Tooltip>
      )}
      {unread && <span className="absolute top-4 right-3 size-1.5 rounded-full bg-accent" aria-label="Unread" />}
    </li>
  );
}
