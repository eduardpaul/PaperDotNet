// Query keys for every server resource the UI caches (TanStack Query). Mutations and live events invalidate by
// these keys, so a prefix (e.g. keys.workspace(id)) covers everything below it.
export const keys = {
  me: ['me'] as const,
  preferences: ['me', 'preferences'] as const,
  home: ['me', 'home'] as const,
  inboxes: ['me', 'inboxes'] as const,
  myTasks: (view: string) => ['me', 'tasks', view] as const,
  myCalendar: (start: string, end: string) => ['me', 'calendar', start, end] as const,
  approvals: (status?: string) => ['me', 'approvals', status ?? 'all'] as const,
  notifications: ['me', 'notifications'] as const,
  subscriptions: ['me', 'subscriptions'] as const,
  unreadCount: ['me', 'notifications', 'unreadCount'] as const,
  workspaces: ['workspaces'] as const,
  workspace: (workspaceId: string) => ['workspaces', workspaceId] as const,
  lists: (workspaceId: string) => ['workspaces', workspaceId, 'lists'] as const,
  list: (workspaceId: string, listId: string) => ['workspaces', workspaceId, 'lists', listId] as const,
  items: (workspaceId: string, listId: string) => ['workspaces', workspaceId, 'lists', listId, 'items'] as const,
  item: (workspaceId: string, listId: string, itemId: string) =>
    ['workspaces', workspaceId, 'lists', listId, 'items', itemId] as const,
  smartFolders: ['smartFolders'] as const,
  search: (query: object) => ['search', query] as const,
};
