import type { ListVersioning } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input, Textarea } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { useCanManageList } from '@/features/list-settings/queries';
import { listBuilder, listQuery } from '@/features/lists/queries';
import { ConfirmDialog, SettingRow, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/')({ component: General });

interface Form {
  name: string;
  description: string;
  allowFolders: boolean;
  versioning: ListVersioning;
  maxVersions: number;
}

/** Name, description, folders and version history (LST-06, LST-11). */
function General() {
  const { workspaceId, listId } = Route.useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  const canManage = useCanManageList(workspaceId, listId);
  const [form, setForm] = useState<Form>();
  const [baseline, setBaseline] = useState<string>();
  const [deleting, setDeleting] = useState(false);
  if (list && list.odataEtag !== baseline) {
    setBaseline(list.odataEtag ?? undefined);
    setForm({
      name: list.name ?? '',
      description: list.description ?? '',
      allowFolders: !!list.allowFolders,
      versioning: list.versioning ?? 'off',
      maxVersions: list.maxVersions ?? 50,
    });
  }
  const save = useMutation({
    meta: { silent: true },
    mutationFn: async (value: Form) =>
      (await listBuilder(workspaceId, listId).patch(
        {
          name: value.name.trim(),
          description: value.description.trim(),
          allowFolders: value.allowFolders,
          versioning: value.versioning,
          maxVersions: value.maxVersions,
        },
        ifMatch(list),
      ))!,
    onSuccess: async (updated) => {
      queryClient.setQueryData(keys.list(workspaceId, listId), updated);
      toast.success('Settings saved.');
      await queryClient.invalidateQueries({ queryKey: keys.lists(workspaceId), exact: true });
    },
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: () => listBuilder(workspaceId, listId).delete(ifMatch(list)),
    onSuccess: async () => {
      toast.success(`“${list?.name}” deleted.`);
      await navigate({ to: '/w/$workspaceId', params: { workspaceId } });
      queryClient.removeQueries({ queryKey: keys.list(workspaceId, listId) });
      await queryClient.invalidateQueries({ queryKey: keys.lists(workspaceId), exact: true });
    },
  });

  if (!list || !form) return <Skeleton className="h-72" />;
  const changed =
    form.name.trim() !== list.name ||
    form.description.trim() !== (list.description ?? '') ||
    form.allowFolders !== list.allowFolders ||
    form.versioning !== list.versioning ||
    form.maxVersions !== list.maxVersions;
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate(form);
  };
  const noun = list.kind === 'library' ? 'library' : 'list';
  return (
    <>
      <form onSubmit={onSubmit}>
        <SettingsSection
          title="General"
          actions={
            <Button
              type="submit"
              variant="primary"
              disabled={!canManage || !changed || !form.name.trim() || save.isPending}
            >
              Save
            </Button>
          }
        >
          <fieldset disabled={!canManage} className="divide-y">
            <SettingRow id="list-name" label="Name">
              <Input
                id="list-name"
                maxLength={200}
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
              />
            </SettingRow>
            <SettingRow id="list-description" label="Description">
              <Textarea
                id="list-description"
                rows={2}
                maxLength={2000}
                value={form.description}
                onChange={(e) => setForm({ ...form, description: e.target.value })}
              />
            </SettingRow>
            <SettingRow id="list-folders" label="Folders" hint="Existing folders stay when this is turned off.">
              <label className="flex items-center gap-2 pt-2 text-[13px]">
                <Checkbox
                  id="list-folders"
                  checked={form.allowFolders}
                  onChange={(e) => setForm({ ...form, allowFolders: e.target.checked })}
                />
                Allow folders
              </label>
            </SettingRow>
            <SettingRow
              id="list-versioning"
              label="Version history"
              hint="Keeps earlier versions of each item to compare and restore. Older versions beyond the limit are removed."
            >
              <div className="flex flex-wrap items-center gap-2">
                <Select
                  id="list-versioning"
                  className="w-44"
                  value={form.versioning}
                  onChange={(e) => setForm({ ...form, versioning: e.target.value as ListVersioning })}
                >
                  <option value="off">Off</option>
                  <option value="major">Keep versions</option>
                </Select>
                {form.versioning !== 'off' && (
                  <>
                    <Input
                      aria-label="Versions to keep"
                      type="number"
                      min={1}
                      max={500}
                      className="w-24"
                      value={form.maxVersions}
                      onChange={(e) => setForm({ ...form, maxVersions: Number(e.target.value) })}
                    />
                    <span className="text-[13px] text-muted">versions</span>
                  </>
                )}
              </div>
            </SettingRow>
          </fieldset>
          {save.isError && <Alert className="mt-3">{problemMessage(save.error)}</Alert>}
        </SettingsSection>
      </form>
      {canManage && (
        <SettingsSection
          title={`Delete ${noun}`}
          description={`Deletes the ${noun} with all its items${list.kind === 'library' ? ' and files' : ''}. This cannot be undone.`}
          actions={
            <Button variant="danger" onClick={() => setDeleting(true)}>
              Delete {noun}
            </Button>
          }
        />
      )}
      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title={`Delete “${list.name}”?`}
        description="Everything in it is deleted for everyone. This cannot be undone."
        confirm={`Delete ${noun}`}
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => remove.mutate()}
      />
    </>
  );
}
