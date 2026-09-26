import type { ContentTypeResponse } from '@paperdotnet/client';
import { ifMatch } from '@paperdotnet/client';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { Pencil, Plus, Shapes, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { listsQuery, workspacesQuery } from '@/api/queries';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Select } from '@/components/ui/select';
import { ContentTypeEditor, fieldTypeLabels } from '@/features/list-settings/content-type-editor';
import { contentTypesQuery, useCanManageList } from '@/features/list-settings/queries';
import { listBuilder, listQuery } from '@/features/lists/queries';
import { fieldLabel, listFields } from '@/features/lists/schema';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/columns')({ component: Columns });

/**
 * A list's columns come from its content types (LST-02, LST-03): each content type is a named set of fields that
 * lists share. Several content types in one list let items of different kinds live together.
 */
function Columns() {
  const { workspaceId, listId } = Route.useParams();
  const queryClient = useQueryClient();
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  const { data: contentTypes } = useQuery(contentTypesQuery);
  const canManage = useCanManageList(workspaceId, listId);
  const [editing, setEditing] = useState<ContentTypeResponse | 'new'>();
  const [removing, setRemoving] = useState<ContentTypeResponse>();
  const [adding, setAdding] = useState('');
  const refresh = (updated?: unknown) =>
    updated
      ? queryClient.setQueryData(keys.list(workspaceId, listId), updated as never)
      : queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) });

  const add = useMutation({
    meta: { silent: true },
    mutationFn: async (contentTypeId: string) =>
      (await listBuilder(workspaceId, listId).contentTypes.post({ contentTypeId }, ifMatch(list)))!,
    onSuccess: (updated) => {
      setAdding('');
      toast.success('Content type added.');
      refresh(updated);
    },
  });
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (contentType: ContentTypeResponse) =>
      listBuilder(workspaceId, listId).contentTypes.byContentTypeId(contentType.id!).delete(ifMatch(list)),
    onSuccess: async () => {
      setRemoving(undefined);
      toast.success('Content type removed from the list.');
      await refresh();
    },
  });

  if (!list) return <Skeleton className="h-72" />;
  const fields = listFields(list);
  const inList = new Set((list.contentTypes ?? []).map((c) => c.id));
  const available = (contentTypes ?? []).filter((c) => !inList.has(c.id));
  const sources = (name: string) =>
    (list.contentTypes ?? []).filter((c) => c.fields?.some((f) => f.name === name)).map((c) => c.name);

  return (
    <>
      <SettingsSection
        title="Columns"
        description="The fields of this list, from its content types. Change them by editing a content type."
        className="px-0 pb-0"
      >
        <div className="overflow-x-auto border-t">
          <table className="w-full text-[13px]">
            <thead>
              <tr className="bg-surface-muted/40 text-left text-xs text-muted">
                <th className="px-5 py-2 font-medium">Column</th>
                <th className="px-3 py-2 font-medium">Type</th>
                <th className="px-5 py-2 font-medium">From</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {fields.map((field) => (
                <tr key={field.name}>
                  <td className="px-5 py-2">
                    <span className="font-medium">{fieldLabel(field)}</span>{' '}
                    <code className="text-xs text-muted">{field.name}</code>
                    {field.required && (
                      <Badge tone="warning" className="ml-2">
                        Required
                      </Badge>
                    )}
                  </td>
                  <td className="px-3 py-2 text-muted">
                    {fieldTypeLabels[field.type ?? ''] ?? field.type}
                    {field.allowMultiple && ', several'}
                  </td>
                  <td className="px-5 py-2 text-muted">
                    {field.name === 'title' ? 'Every item' : sources(field.name!).join(', ')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </SettingsSection>

      <SettingsSection
        title="Content types"
        description="Content types belong to the organization: editing one changes every list that uses it."
        className="px-0 pb-0"
        actions={
          canManage && (
            <>
              <Select
                aria-label="Content type to add"
                className="mr-auto w-56"
                value={adding}
                onChange={(e) => setAdding(e.target.value)}
              >
                <option value="">Add an existing one…</option>
                {available.map((c) => (
                  <option key={c.id} value={c.id!}>
                    {c.name}
                  </option>
                ))}
              </Select>
              <Button disabled={!adding || add.isPending} onClick={() => add.mutate(adding)}>
                Add
              </Button>
              <Button variant="primary" onClick={() => setEditing('new')}>
                <Plus /> New content type
              </Button>
            </>
          )
        }
      >
        <ul className="divide-y border-t">
          {(list.contentTypes ?? []).map((contentType) => (
            <li key={contentType.id} className="flex items-center gap-3 px-5 py-3">
              <Shapes className="size-4 text-muted" />
              <div className="min-w-0 flex-1">
                <p className="flex items-center gap-2 text-[13px] font-medium">
                  {contentType.name}
                  {contentType.isBuiltIn && <Badge>Built in</Badge>}
                  {contentType.extensionId && <Badge>{contentType.extensionId}</Badge>}
                </p>
                <p className="truncate text-xs text-muted">
                  {contentType.fields?.length
                    ? contentType.fields.map((f) => fieldLabel(f)).join(', ')
                    : 'Only a title'}
                </p>
              </div>
              <Button
                size="sm"
                variant="ghost"
                aria-label={`Edit ${contentType.name}`}
                onClick={() => setEditing(contentType)}
              >
                <Pencil /> {canManage && !contentType.extensionId ? 'Edit' : 'View'}
              </Button>
              {canManage && (list.contentTypes?.length ?? 0) > 1 && (
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={`Remove ${contentType.name} from the list`}
                  onClick={() => setRemoving(contentType)}
                >
                  <Trash2 />
                </Button>
              )}
            </li>
          ))}
        </ul>
        {add.isError && <Alert className="m-5">{problemMessage(add.error)}</Alert>}
      </SettingsSection>

      {editing && (
        <EditorWithUsage
          contentType={editing === 'new' ? undefined : (contentTypes?.find((c) => c.id === editing.id) ?? editing)}
          canManage={canManage}
          onSaved={async (saved) => {
            if (editing === 'new') {
              const updated = await listBuilder(workspaceId, listId).contentTypes.post(
                { contentTypeId: saved.id! },
                ifMatch(list),
              );
              refresh(updated);
            } else await refresh();
          }}
          onClose={() => setEditing(undefined)}
        />
      )}
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(open) => !open && setRemoving(undefined)}
        title={`Remove “${removing?.name ?? ''}” from this list?`}
        description="The content type stays in the organization. Items of this type must be deleted or changed first."
        confirm="Remove"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </>
  );
}

/** The editor, told which lists use the content type (loaded when it opens). */
function EditorWithUsage({
  contentType,
  canManage,
  onSaved,
  onClose,
}: {
  contentType?: ContentTypeResponse;
  canManage: boolean;
  onSaved: (saved: ContentTypeResponse) => Promise<void>;
  onClose: () => void;
}) {
  const { data: workspaces } = useQuery(workspacesQuery);
  const summaries = useQueries({ queries: (workspaces ?? []).map((w) => listsQuery(w.id!)) }).flatMap(
    (q) => q.data ?? [],
  );
  const lists = useQueries({
    queries: contentType ? summaries.map((l) => listQuery(l.workspaceId!, l.id!)) : [],
  }).flatMap((q) => (q.data ? [q.data] : []));
  const usedBy = lists.filter((l) => l.contentTypes?.some((c) => c.id === contentType?.id)).map((l) => l.name!);
  return (
    <ContentTypeEditor
      contentType={contentType}
      usedBy={usedBy}
      canManage={canManage}
      onSaved={onSaved}
      onClose={onClose}
    />
  );
}
