import { queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { workspaceBuilder } from '@/features/workspaces/queries';

export const workflowsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: [...keys.workspace(workspaceId), 'workflows'],
    queryFn: async () => (await workspaceBuilder(workspaceId).workflows.get()) ?? [],
  });

export const triggerCatalogQuery = queryOptions({
  queryKey: ['workflows', 'triggers'],
  queryFn: async () => (await api.v10.workflows.triggers.get()) ?? [],
  staleTime: Infinity,
});

export const actionCatalogQuery = queryOptions({
  queryKey: ['workflows', 'activities'],
  queryFn: async () => (await api.v10.workflows.activities.get()) ?? [],
  staleTime: Infinity,
});
