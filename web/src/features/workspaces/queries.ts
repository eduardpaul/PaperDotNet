import { queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';

export const workspaceBuilder = (workspaceId: string) => api.v10.workspaces.byWorkspaceId(workspaceId);

export const workspaceQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: keys.workspace(workspaceId),
    queryFn: async () => (await workspaceBuilder(workspaceId).get())!,
  });

export const membersQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: [...keys.workspace(workspaceId), 'members'],
    queryFn: async () => (await workspaceBuilder(workspaceId).members.get()) ?? [],
  });
