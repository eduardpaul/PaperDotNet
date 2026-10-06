import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useState } from 'react';
import { keys } from '@/api/keys';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Checkbox } from '@/components/ui/select';
import { workspaceQuery } from '@/features/workspaces/queries';
import { listBuilder } from '@/features/lists/queries';
import { SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export function SearchSettings({ workspaceId, listId }: { workspaceId: string; listId: string }) {
  const queryClient = useQueryClient();
  const [pendingInclusion, setPendingInclusion] = useState<boolean>();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const canManage = workspace?.access === 'manage';
  const queryKey = keys.searchSettings(workspaceId, listId);
  const { data: settings } = useQuery({
    queryKey,
    queryFn: () => listBuilder(workspaceId, listId).searchSettings.get(),
  });
  const save = useMutation({
    meta: { silent: true },
    mutationFn: (included: boolean) =>
      listBuilder(workspaceId, listId).searchSettings.put({ included }, ifMatch(settings)),
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKey, updated);
      toast.success(updated?.included ? 'Included in search. Reindexing requested.' : 'Excluded from search.');
    },
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) });
      await queryClient.invalidateQueries({ queryKey: ['search'] });
      setPendingInclusion(undefined);
    },
  });
  return (
    <SettingsSection title="Search" description="Choose whether items from this list appear in search.">
      {!settings ? (
        <Skeleton className="h-12" />
      ) : (
        <label className="flex items-start gap-3">
          <Checkbox
            aria-label="Include this list in search"
            checked={pendingInclusion ?? !!settings.included}
            disabled={!canManage || save.isPending}
            onChange={(event) => {
              setPendingInclusion(event.target.checked);
              save.mutate(event.target.checked);
            }}
          />
          <span className="flex flex-col gap-1">
            <span className="text-sm">Include this list in search</span>
            <span className="text-xs text-muted">
              Excluding hides titles, fields, comments and file text immediately. Including requests a rebuild through
              indexing workflows.
            </span>
          </span>
        </label>
      )}
      {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
    </SettingsSection>
  );
}
