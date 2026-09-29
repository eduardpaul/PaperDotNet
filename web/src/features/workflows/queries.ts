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

/** The actions steps can run (the catalog also lists the flow activities, which steps express as step types). */
export const actionCatalogQuery = queryOptions({
  queryKey: ['workflows', 'activities'],
  queryFn: async () => ((await api.v10.workflows.activities.get()) ?? []).filter((a) => a.kind === 'action'),
  staleTime: Infinity,
});
