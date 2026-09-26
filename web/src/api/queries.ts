// Queries shared by several screens. Screen-specific queries live next to their screen (features/<area>).
import { all, toArray } from '@paperdotnet/client';
import { queryOptions } from '@tanstack/react-query';
import { api } from './client';
import { keys } from './keys';

export const meQuery = queryOptions({
  queryKey: keys.me,
  queryFn: async () => (await api.v10.me.get())!,
  staleTime: 5 * 60_000,
});

export const preferencesQuery = queryOptions({
  queryKey: keys.preferences,
  queryFn: async () => (await api.v10.me.preferences.get())!,
  staleTime: 5 * 60_000,
});

export const homeQuery = queryOptions({
  queryKey: keys.home,
  queryFn: async () => (await api.v10.me.home.get())!,
  staleTime: Infinity,
});

export const inboxesQuery = queryOptions({
  queryKey: keys.inboxes,
  queryFn: async () => (await api.v10.me.inboxes.get()) ?? [],
});

export const workspacesQuery = queryOptions({
  queryKey: keys.workspaces,
  queryFn: () => toArray(all(api.v10.workspaces, { queryParameters: { top: 200 } })),
});

export const listsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: keys.lists(workspaceId),
    queryFn: async () => (await api.v10.workspaces.byWorkspaceId(workspaceId).lists.get()) ?? [],
  });

export const unreadCountQuery = queryOptions({
  queryKey: keys.unreadCount,
  queryFn: async () => (await api.v10.me.notifications.unreadCount.get())?.count ?? 0,
});

export const notificationsQuery = queryOptions({
  queryKey: keys.notifications,
  queryFn: async () => (await api.v10.me.notifications.get({ queryParameters: { top: 30 } }))?.value ?? [],
});

export const myTasksQuery = (view: 'mine' | 'dueThisWeek' | 'overdue' | 'all') =>
  queryOptions({
    queryKey: keys.myTasks(view),
    queryFn: async () => (await api.v10.me.tasks.get({ queryParameters: { view, top: 100 } }))?.value ?? [],
  });

export const myCalendarQuery = (start: Date, end: Date) =>
  queryOptions({
    queryKey: keys.myCalendar(start.toISOString(), end.toISOString()),
    queryFn: async () =>
      (await api.v10.me.calendar.get({ queryParameters: { start, end, includeTasks: true } }))?.value ?? [],
  });

export const pendingApprovalsQuery = queryOptions({
  queryKey: keys.approvals('pending'),
  queryFn: async () =>
    (await api.v10.me.approvals.get({ queryParameters: { status: 'pending', top: 50 } }))?.value ?? [],
});
