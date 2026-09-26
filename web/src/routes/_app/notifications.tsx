import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { Bell, CheckCheck } from 'lucide-react';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { notificationsQuery, unreadCountQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { NotificationRow } from '@/features/notifications/notification-row';

export const Route = createFileRoute('/_app/notifications')({ component: Notifications });

function Notifications() {
  const queryClient = useQueryClient();
  const { data, isPending } = useQuery(notificationsQuery);
  const { data: unread } = useQuery(unreadCountQuery);
  const markAll = useMutation({
    mutationFn: () => api.v10.me.notifications.read.post(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.notifications }),
  });

  return (
    <Page className="max-w-3xl">
      <PageHeader
        title="Notifications"
        description={unread ? `${unread} unread` : 'Mentions, reminders, approvals and changes you follow.'}
        actions={
          <Button disabled={!unread || markAll.isPending} onClick={() => markAll.mutate()}>
            <CheckCheck /> Mark all read
          </Button>
        }
      />
      <Card>
        {isPending ? (
          <div className="flex flex-col gap-3 p-4">
            <Skeleton className="h-12" />
            <Skeleton className="h-12" />
            <Skeleton className="h-12" />
          </div>
        ) : data?.length ? (
          <ul className="divide-y">
            {data.map((n) => (
              <NotificationRow key={n.id} notification={n} />
            ))}
          </ul>
        ) : (
          <EmptyState icon={Bell} title="No notifications">
            You will be notified about mentions, reminders, approvals and items you follow.
          </EmptyState>
        )}
      </Card>
    </Page>
  );
}
