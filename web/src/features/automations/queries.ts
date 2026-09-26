import { queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { workspaceBuilder } from '@/features/workspaces/queries';

export const automationsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: [...keys.workspace(workspaceId), 'automations'],
    queryFn: async () => (await workspaceBuilder(workspaceId).automations.get()) ?? [],
  });

export const triggerCatalogQuery = queryOptions({
  queryKey: ['automation', 'triggers'],
  queryFn: async () => (await api.v10.automation.triggers.get()) ?? [],
  staleTime: Infinity,
});

export const actionCatalogQuery = queryOptions({
  queryKey: ['automation', 'actions'],
  queryFn: async () => (await api.v10.automation.actions.get()) ?? [],
  staleTime: Infinity,
});
