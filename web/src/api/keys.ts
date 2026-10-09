// Query keys for every server resource the UI caches (TanStack Query). Mutations and live events invalidate by
// these keys, so a prefix (e.g. keys.workspace(id)) covers everything below it.
export const keys = {
  globalItemResources: ['globalItems'] as const,
  globalItems: (q: string, writable: boolean) => ['globalItems', 'search', q, writable] as const,
  globalItem: (itemId: string) => ['globalItems', itemId] as const,
  relations: (itemId: string) => ['globalItems', itemId, 'relations'] as const,
  me: ['me'] as const,
  preferences: ['me', 'preferences'] as const,
  home: ['me', 'home'] as const,
  inboxes: ['me', 'inboxes'] as const,
  myTasks: (view: string) => ['me', 'tasks', view] as const,
  myCalendar: (start: string, end: string) => ['me', 'calendar', start, end] as const,
  approvals: (status?: string) => ['me', 'approvals', status ?? 'all'] as const,
  approvalReview: (approvalId: string) => ['me', 'approvals', approvalId, 'review'] as const,
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
  calendarSources: (workspaceId: string, listId: string) =>
    ['workspaces', workspaceId, 'lists', listId, 'calendarSources'] as const,
  /** A library's document workflows (ADR-0038: reading the text, thumbnails, pages, OCR). */
  libraryWorkflows: (workspaceId: string, listId: string) =>
    ['workspaces', workspaceId, 'lists', listId, 'workflows', 'builtIns'] as const,
  /** The organization's users (the directory). */
  users: ['users'] as const,
  /** Users by id, e.g. the names of selected values (ids sorted, so the key is stable). */
  usersById: (ids: readonly string[]) => ['users', 'byId', ids] as const,
  groups: ['groups'] as const,
  group: (groupId: string) => ['groups', groupId] as const,
  groupMembers: (groupId: string) => ['groups', groupId, 'members'] as const,
  groupInbox: (groupId: string) => ['groups', groupId, 'inbox'] as const,
  nestedGroups: (groupId: string) => ['groups', groupId, 'groups'] as const,
  /**
   * People that can be picked (enabled, not service accounts). Limited to a group, they are its effective members and
   * the key is under the groups, so membership changes (which invalidate keys.groups) refresh them.
   */
  assignablePeople: (memberOf?: string): readonly string[] =>
    memberOf ? ['groups', memberOf, 'assignable'] : ['users', 'assignable'],
  smartFolders: ['smartFolders'] as const,
  search: (query: object) => ['search', query] as const,
};
