import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input, Textarea } from '@/components/ui/input';
import { ConfirmDialog, SettingRow, SettingsSection } from '@/features/settings/section';
import { workspaceBuilder, workspaceQuery } from '@/features/workspaces/queries';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/settings/')({ component: General });

function General() {
  const { workspaceId } = Route.useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { data: workspace } = useQuery(workspaceQuery(workspaceId));
  const [name, setName] = useState(workspace?.name ?? '');
  const [description, setDescription] = useState(workspace?.description ?? '');
  const [baseline, setBaseline] = useState(workspace?.odataEtag);
  const [deleting, setDeleting] = useState(false);
  if (workspace && workspace.odataEtag !== baseline) {
    setBaseline(workspace.odataEtag);
    setName(workspace.name ?? '');
    setDescription(workspace.description ?? '');
  }
  const save = useMutation({
    meta: { silent: true },
    mutationFn: async () =>
      (await workspaceBuilder(workspaceId).patch(
        { name: name.trim(), description: description.trim() },
        ifMatch(workspace),
      ))!,
    onSuccess: async (updated) => {
      queryClient.setQueryData(keys.workspace(workspaceId), updated);
      toast.success('Workspace saved.');
      await queryClient.invalidateQueries({ queryKey: keys.workspaces, exact: true });
    },
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: () => workspaceBuilder(workspaceId).delete(ifMatch(workspace)),
    onSuccess: async () => {
      toast.success(`“${workspace?.name}” deleted.`);
      await navigate({ to: '/w' });
      queryClient.removeQueries({ queryKey: keys.workspace(workspaceId) });
      await queryClient.invalidateQueries({ queryKey: keys.workspaces, exact: true });
    },
  });

  if (!workspace) return <Skeleton className="h-64" />;
  const canManage = workspace.access === 'manage';
  const changed = name.trim() !== workspace.name || description.trim() !== (workspace.description ?? '');
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <>
      <form onSubmit={onSubmit}>
        <SettingsSection
          title="General"
          actions={
            <Button type="submit" variant="primary" disabled={!canManage || !changed || !name.trim() || save.isPending}>
              Save
            </Button>
          }
        >
          <div className="divide-y">
            <SettingRow id="ws-name" label="Name">
              <Input
                id="ws-name"
                value={name}
                maxLength={200}
                disabled={!canManage}
                onChange={(e) => setName(e.target.value)}
              />
            </SettingRow>
            <SettingRow id="ws-description" label="Description" hint="Shown under the name.">
              <Textarea
                id="ws-description"
                rows={3}
                value={description}
                maxLength={2000}
                disabled={!canManage}
                onChange={(e) => setDescription(e.target.value)}
              />
            </SettingRow>
          </div>
          {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
        </SettingsSection>
      </form>
      {canManage && !workspace.isPersonal && (
        <SettingsSection
          title="Delete workspace"
          description="Deletes the workspace with all its lists, items, documents and automations. This cannot be undone."
          actions={
            <Button variant="danger" onClick={() => setDeleting(true)}>
              Delete workspace
            </Button>
          }
        />
      )}
      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title={`Delete “${workspace.name}”?`}
        description="Everything in it is deleted for everyone. This cannot be undone."
        confirm="Delete workspace"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => remove.mutate()}
      />
    </>
  );
}
