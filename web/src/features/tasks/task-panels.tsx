import type { ChecklistEntryDto, LinkedItem } from '@paperdotnet/client';
import { fields as fieldValues, fieldsOf, isStatus } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Check, GripVertical, Link2, Plus, X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Combobox } from '@/components/ui/combobox';
import { Alert, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox } from '@/components/ui/select';
import type { ItemPanelContext } from '@/extensibility/item-panels';
import { choiceLabel } from '@/features/fields/values';
import { listBuilder, odataString } from '@/features/lists/queries';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';
import { RepeatEditor } from './repeat-editor';

const itemKey = ({ workspaceId, list, item }: ItemPanelContext) => keys.item(workspaceId, list.id!, item.id!);

/** A task's checklist (TSK-01): saved as a whole on every change. */
export function ChecklistTab(context: ItemPanelContext) {
  const { workspaceId, list, item } = context;
  const queryClient = useQueryClient();
  const builder = listBuilder(workspaceId, list.id!).items.byItemId(item.id!).checklist;
  const key = [...itemKey(context), 'checklist'];
  const { data, isPending } = useQuery({ queryKey: key, queryFn: async () => (await builder.get())?.value ?? [] });
  const [text, setText] = useState('');
  // Saves run one after another (quick clicks must not overtake each other); each response is the whole list.
  const saving = useMutation({
    scope: { id: `checklist-${item.id}` },
    mutationFn: (entries: ChecklistEntryDto[]) => builder.put(entries),
    onSuccess: (response) => queryClient.setQueryData(key, response?.value ?? []),
    onError: () => queryClient.invalidateQueries({ queryKey: key }),
  });
  // Changes show at once (local state, so a checkbox keeps its click); the saves follow in order.
  const [local, setLocal] = useState<ChecklistEntryDto[]>();
  const save = {
    mutate: (entries: ChecklistEntryDto[]) => {
      setLocal(entries);
      saving.mutate(entries);
    },
  };
  const entries = local ?? data ?? [];
  const done = entries.filter((e) => e.done).length;

  const add = (event: FormEvent) => {
    event.preventDefault();
    if (!text.trim()) return;
    save.mutate([...entries, { text: text.trim(), done: false }]);
    setText('');
  };

  if (isPending) return <Skeleton className="m-5 h-24" />;
  return (
    <div className="flex flex-col gap-3 p-5">
      {entries.length > 0 && (
        <div className="flex items-center gap-3">
          <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-muted">
            <div className="h-full bg-success transition-all" style={{ width: `${(done / entries.length) * 100}%` }} />
          </div>
          <span className="text-xs text-muted tabular-nums">
            {done} of {entries.length}
          </span>
        </div>
      )}
      <ul className="flex flex-col gap-1">
        {entries.map((entry, index) => (
          <li key={index} className="group flex items-center gap-2 rounded-md px-1 py-1 hover:bg-surface-muted/60">
            <GripVertical className="size-3.5 text-muted/40" />
            <Checkbox
              aria-label={entry.text ?? ''}
              checked={!!entry.done}
              onChange={() => save.mutate(entries.map((e, i) => (i === index ? { ...e, done: !e.done } : e)))}
            />
            <span className={cn('flex-1 text-[13px]', entry.done && 'text-muted line-through')}>{entry.text}</span>
            <button
              type="button"
              aria-label={`Remove ${entry.text}`}
              className="text-muted opacity-0 group-hover:opacity-100 hover:text-danger focus-visible:opacity-100"
              onClick={() => save.mutate(entries.filter((_, i) => i !== index))}
            >
              <X className="size-3.5" />
            </button>
          </li>
        ))}
      </ul>
      <form onSubmit={add} className="flex gap-2">
        <Input
          aria-label="New checklist entry"
          placeholder="Add a step…"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <Button type="submit" size="md" disabled={!text.trim()}>
          <Plus /> Add
        </Button>
      </form>
    </div>
  );
}

function LinkedRow({ link, onRemove }: { link: LinkedItem; onRemove?: () => void }) {
  return (
    <li className="flex items-center gap-2 py-1.5 text-[13px]">
      {link.status === 'completed' ? (
        <Check className="size-3.5 text-success" />
      ) : (
        <Link2 className="size-3.5 text-muted" />
      )}
      <Link
        to="/w/$workspaceId/l/$listId"
        params={{ workspaceId: link.workspaceId!, listId: link.listId! }}
        search={{ item: link.itemId! }}
        className={cn(
          'min-w-0 flex-1 truncate hover:underline',
          link.status === 'completed' && 'text-muted line-through',
        )}
      >
        {link.title}
      </Link>
      {link.status && link.status !== 'completed' && <Badge>{choiceLabel(link.status)}</Badge>}
      {onRemove && (
        <button
          type="button"
          aria-label={`Unlink ${link.title}`}
          className="text-muted hover:text-danger"
          onClick={onRemove}
        >
          <X className="size-3.5" />
        </button>
      )}
    </li>
  );
}

/** Subtasks, "blocked by" dependencies and linked documents (TSK-02, TSK-06). */
export function RelatedTab(context: ItemPanelContext) {
  const { workspaceId, list, item } = context;
  const queryClient = useQueryClient();
  const items = listBuilder(workspaceId, list.id!).items;
  const key = [...itemKey(context), 'links'];
  const { data, isPending } = useQuery({
    queryKey: key,
    queryFn: async () => (await items.byItemId(item.id!).links.get())!,
  });
  const [subtask, setSubtask] = useState('');
  const [search, setSearch] = useState('');
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: key }),
      queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, list.id!) }),
    ]);
  const { data: candidates, isFetching } = useQuery({
    queryKey: [...keys.items(workspaceId, list.id!), 'blockers', search],
    queryFn: async () =>
      (
        await items.get({
          queryParameters: {
            filter: `id ne ${item.id} and isFolder eq false${search ? ` and contains(tolower(fields/title),${odataString(search.toLowerCase())})` : ''}`,
            select: 'title',
            top: 20,
          },
        })
      )?.value ?? [],
  });
  const addSubtask = useMutation({
    meta: { silent: true },
    mutationFn: async (title: string) => {
      const created = await items.post({ contentTypeId: item.contentTypeId, fields: fieldValues({ title }) });
      await items.byItemId(item.id!).links.post({ kind: 'subtask', itemId: created!.id, listId: list.id, workspaceId });
    },
    onSuccess: async () => {
      setSubtask('');
      await refresh();
    },
  });
  const addBlocker = useMutation({
    meta: { silent: true },
    mutationFn: (blockerId: string) =>
      items.byItemId(item.id!).links.post({ kind: 'blockedBy', itemId: blockerId, listId: list.id, workspaceId }),
    onSuccess: refresh,
    onError: (error) =>
      toast.error(
        isStatus(error, 409) || isStatus(error, 400) ? problemMessage(error) : 'The link could not be added.',
      ),
  });
  const unlink = useMutation({
    mutationFn: (linkId: string) => items.byItemId(item.id!).links.byLinkId(linkId).delete(),
    onSuccess: refresh,
  });

  if (isPending) return <Skeleton className="m-5 h-24" />;
  const section = (title: string, links: LinkedItem[] | null | undefined, removable = true) =>
    links?.length ? (
      <section>
        <h3 className="mb-1 text-xs font-medium text-muted">{title}</h3>
        <ul className="divide-y">
          {links.map((l) => (
            <LinkedRow
              key={l.linkId ?? l.itemId}
              link={l}
              onRemove={removable && l.linkId ? () => unlink.mutate(l.linkId!) : undefined}
            />
          ))}
        </ul>
      </section>
    ) : null;

  return (
    <div className="flex flex-col gap-5 p-5">
      {data?.parent && section('Part of', [data.parent], false)}
      <section className="flex flex-col gap-2">
        <h3 className="text-xs font-medium text-muted">Subtasks</h3>
        {data?.subtasks?.length ? (
          <ul className="divide-y">
            {data.subtasks.map((l) => (
              <LinkedRow key={l.linkId} link={l} onRemove={() => unlink.mutate(l.linkId!)} />
            ))}
          </ul>
        ) : null}
        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (subtask.trim()) addSubtask.mutate(subtask.trim());
          }}
        >
          <Input
            aria-label="New subtask"
            placeholder="Add a subtask…"
            value={subtask}
            onChange={(e) => setSubtask(e.target.value)}
          />
          <Button type="submit" disabled={!subtask.trim() || addSubtask.isPending}>
            <Plus /> Add
          </Button>
        </form>
        {addSubtask.isError && <Alert>{problemMessage(addSubtask.error)}</Alert>}
      </section>
      <section className="flex flex-col gap-2">
        <h3 id="blocked-by-label" className="text-xs font-medium text-muted">
          Blocked by
        </h3>
        {data?.blockedBy?.length ? (
          <ul className="divide-y">
            {data.blockedBy.map((l) => (
              <LinkedRow key={l.linkId} link={l} onRemove={() => unlink.mutate(l.linkId!)} />
            ))}
          </ul>
        ) : null}
        <Combobox
          aria-labelledby="blocked-by-label"
          placeholder="Add a task this one waits for…"
          loading={isFetching}
          onSearch={setSearch}
          selected={[]}
          options={(candidates ?? []).map((c) => ({ value: c.id!, label: String(fieldsOf(c).title ?? 'Untitled') }))}
          onChange={(options) => options[0] && addBlocker.mutate(options[0].value)}
        />
      </section>
      {section('Blocking', data?.blocking, false)}
      {section('Documents', data?.documents)}
    </div>
  );
}

/** Repeats a task (TSK-05): completing it creates the next one. */
export function TaskRepeatTab(context: ItemPanelContext) {
  const { workspaceId, list, item } = context;
  const queryClient = useQueryClient();
  const builder = listBuilder(workspaceId, list.id!).items.byItemId(item.id!).recurrence;
  const key = [...itemKey(context), 'recurrence'];
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
  const save = useMutation({
    mutationFn: (rule: string) => builder.put({ rule }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: key }),
  });
  const remove = useMutation({
    mutationFn: () => builder.delete(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: key }),
  });
  if (isPending) return <Skeleton className="m-5 h-24" />;
  return (
    <div className="p-5">
      {!data && !fieldsOf(item).dueDate && (
        <Alert tone="warning" className="mb-3">
          Give the task a due date first: each next task is due one repeat later.
        </Alert>
      )}
      {save.isError && <Alert className="mb-3">{problemMessage(save.error)}</Alert>}
      <RepeatEditor
        key={data?.rule ?? 'none'}
        rule={data?.rule ?? undefined}
        busy={save.isPending || remove.isPending}
        onSave={(rule) => save.mutate(rule)}
        onRemove={() => remove.mutate()}
      />
      {data?.nextDueDate && <p className="mt-3 text-xs text-muted">Next due {String(data.nextDueDate)}</p>}
    </div>
  );
}
