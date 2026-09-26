import { isStatus } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { keys } from '@/api/keys';
import { Alert, Skeleton } from '@/components/ui/feedback';
import type { ItemPanelContext } from '@/extensibility/item-panels';
import { RepeatEditor } from '@/features/tasks/repeat-editor';
import { listBuilder } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';
import { useFormat } from '@/lib/preferences';

/** Repeats an event (CAL-02) in the user's time zone; skipped and moved occurrences are listed. */
export function SeriesTab({ workspaceId, list, item }: ItemPanelContext) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const builder = listBuilder(workspaceId, list.id!).items.byItemId(item.id!).series;
  const key = [...keys.item(workspaceId, list.id!, item.id!), 'series'];
  const { data, isPending } = useQuery({
    queryKey: key,
    retry: false,
    queryFn: async () => {
      try {
        return (await builder.get()) ?? null;
      } catch (error) {
        if (isStatus(error, 404)) return null;
        throw error;
      }
    },
  });
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: key }),
      queryClient.invalidateQueries({ queryKey: ['me', 'calendar'] }),
    ]);
  const save = useMutation({
    mutationFn: (rule: string) => builder.put({ rule, timeZone: data?.timeZone ?? format.preferences.timeZone }),
    onSuccess: refresh,
  });
  const remove = useMutation({ mutationFn: () => builder.delete(), onSuccess: refresh });
  if (isPending) return <Skeleton className="m-5 h-24" />;
  return (
    <div className="flex flex-col gap-4 p-5">
      {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
      <RepeatEditor
        key={data?.rule ?? 'none'}
        rule={data?.rule ?? undefined}
        busy={save.isPending || remove.isPending}
        onSave={(rule) => save.mutate(rule)}
        onRemove={() => remove.mutate()}
      />
      {data && <p className="text-xs text-muted">Times are in {data.timeZone}.</p>}
      {!!data?.cancelled?.length && (
        <section>
          <h3 className="mb-1 text-xs font-medium text-muted">Skipped</h3>
          <ul className="text-[13px]">
            {data.cancelled.map((start) => (
              <li key={String(start)}>{format.dateTime(start)}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
