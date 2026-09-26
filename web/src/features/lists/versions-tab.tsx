import type { ItemResponse, ItemVersionResponse, ListResponse } from '@paperdotnet/client';
import { fieldsOf, ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { History, RotateCcw } from 'lucide-react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { userName } from '@/features/fields/directory';
import { FieldValue } from '@/features/fields/display';
import { ValueNamesProvider, useValueNames } from '@/features/fields/lookups';
import { useFormat } from '@/lib/preferences';
import { listBuilder, versionsQuery } from './queries';
import { fieldLabel, listFields } from './schema';

/** Version history (LST-12): what changed in each version, and restoring one as a new version. */
export function VersionsTab({
  workspaceId,
  list,
  item,
}: {
  workspaceId: string;
  list: ListResponse;
  item: ItemResponse;
}) {
  const { data, isPending } = useQuery(versionsQuery(workspaceId, list.id!, item.id!));
  const fields = listFields(list);

  if (list.versioning === 'off') {
    return (
      <EmptyState icon={History} title="Version history is off for this list">
        An owner can turn it on in the list settings.
      </EmptyState>
    );
  }

  if (isPending)
    return (
      <div className="space-y-3 p-5">
        <Skeleton className="h-16" />
        <Skeleton className="h-16" />
      </div>
    );

  return (
    <ValueNamesProvider fields={fields} values={(data ?? []).map((v) => fieldsOf(v))}>
      <ol className="divide-y">
        {(data ?? []).map((version) => (
          <VersionRow
            key={version.number}
            workspaceId={workspaceId}
            list={list}
            item={item}
            version={version}
            fields={fields}
          />
        ))}
      </ol>
    </ValueNamesProvider>
  );
}

function VersionRow({
  workspaceId,
  list,
  item,
  version,
  fields,
}: {
  workspaceId: string;
  list: ListResponse;
  item: ItemResponse;
  version: ItemVersionResponse;
  fields: ReturnType<typeof listFields>;
}) {
  const format = useFormat();
  const names = useValueNames();
  const queryClient = useQueryClient();
  const values = fieldsOf(version);
  const changed = (version.changedFields ?? []).map(
    (name) => fields.find((f) => f.name === name) ?? { name, type: 'text' },
  );
  const restore = useMutation({
    mutationFn: () =>
      listBuilder(workspaceId, list.id!)
        .items.byItemId(item.id!)
        .versions.byNumber(version.number!)
        .restore.post(ifMatch(item)),
    onSuccess: async (restored) => {
      toast.success(`Version ${version.number} restored as a new version.`);
      if (restored) queryClient.setQueryData(keys.item(workspaceId, list.id!, item.id!), restored);
      await queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, list.id!) });
    },
  });

  return (
    <li className="px-5 py-4">
      <div className="mb-2 flex items-center gap-2">
        <span className="font-semibold">Version {version.number}</span>
        {version.isCurrent && <Badge tone="accent">Current</Badge>}
        <span className="ml-auto text-xs text-muted" title={format.dateTime(version.createdAt)}>
          {userName(names.users.get(version.createdBy ?? ''), version.createdBy ?? '')} ·{' '}
          {format.relative(version.createdAt)}
        </span>
      </div>
      {changed.length ? (
        <dl className="grid grid-cols-[minmax(0,10rem)_1fr] gap-x-3 gap-y-1.5 text-[13px]">
          {changed.map((field) => (
            <div key={field.name} className="contents">
              <dt className="truncate text-muted">{fieldLabel(field)}</dt>
              <dd className="min-w-0">
                <FieldValue field={field} value={values[field.name!]} />
              </dd>
            </div>
          ))}
        </dl>
      ) : (
        <p className="text-[13px] text-muted">Created.</p>
      )}
      {!version.isCurrent && (
        <Button size="sm" className="mt-3" disabled={restore.isPending} onClick={() => restore.mutate()}>
          <RotateCcw /> Restore this version
        </Button>
      )}
    </li>
  );
}
