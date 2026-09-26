import type { MyTask } from '@paperdotnet/client';
import { fields as fieldValues, ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { CircleCheckBig, ListChecks, Plus } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { meQuery, myTasksQuery } from '@/api/queries';
import { Page, PageHeader } from '@/components/page';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { choiceLabel } from '@/features/fields/values';
import { ItemPanelLoader } from '@/features/lists/item-panel-loader';
import { rememberedList, rememberList, useListsByTemplate } from '@/features/lists/lists-by-template';
import { listBuilder } from '@/features/lists/queries';
import { useFormat } from '@/lib/preferences';
import { useNow } from '@/lib/use-now';
import { cn } from '@/lib/utils';

type View = 'mine' | 'dueThisWeek' | 'overdue' | 'all';
interface TasksSearch {
  view?: View;
  /** The open task: "workspaceId/listId/itemId". */
  open?: string;
  tab?: string;
}

const views: { key: View; label: string }[] = [
  { key: 'mine', label: 'Assigned to me' },
  { key: 'dueThisWeek', label: 'Due this week' },
  { key: 'overdue', label: 'Overdue' },
  { key: 'all', label: 'All open' },
];

export const Route = createFileRoute('/_app/tasks')({
  validateSearch: (search: Record<string, unknown>): TasksSearch => ({
    view: views.some((v) => v.key === search.view) ? (search.view as View) : undefined,
    open: typeof search.open === 'string' && search.open.split('/').length === 3 ? search.open : undefined,
    tab: typeof search.tab === 'string' ? search.tab : undefined,
  }),
  component: Tasks,
});

/** Open tasks across every task list (TSK-03), grouped by when they are due. */
function Tasks() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const format = useFormat();
  const now = useNow();
  const today = format.dayKey(now);
  const view = search.view ?? 'mine';
  const { data, isPending } = useQuery(myTasksQuery(view));
  const setSearch = (patch: Partial<TasksSearch>, replace = false) =>
    void navigate({ search: (c) => ({ ...c, ...patch }), replace });
  const [openWs, openList, openItem] = search.open?.split('/') ?? [];

  const weekEnd = format.dayKey(new Date(now.getTime() + 7 * 86_400_000));
  const groups: { title: string; tasks: MyTask[]; tone?: 'danger' }[] = [
    { title: 'Overdue', tasks: [], tone: 'danger' },
    { title: 'Today', tasks: [] },
    { title: 'Next 7 days', tasks: [] },
    { title: 'Later', tasks: [] },
    { title: 'No due date', tasks: [] },
  ];
  for (const task of data ?? []) {
    const due = task.dueDate ?? undefined;
    const index = !due ? 4 : due < today ? 0 : due === today ? 1 : due <= weekEnd ? 2 : 3;
    groups[index]!.tasks.push(task);
  }

  return (
    <Page className="max-w-4xl">
      <PageHeader
        icon={ListChecks}
        title="My tasks"
        description="Open tasks from every task list, by when they are due."
      />
      <QuickAdd />
      <div role="tablist" aria-label="Task view" className="mb-4 inline-flex rounded-md bg-surface-muted p-0.5">
        {views.map((v) => (
          <button
            key={v.key}
            role="tab"
            type="button"
            aria-selected={view === v.key}
            onClick={() => setSearch({ view: v.key === 'mine' ? undefined : v.key })}
            className={cn(
              'rounded px-2.5 py-1 text-xs font-medium text-muted',
              view === v.key && 'bg-surface text-foreground shadow-xs',
            )}
          >
            {v.label}
          </button>
        ))}
      </div>
      {isPending ? (
        <Skeleton className="h-48" />
      ) : data?.length ? (
        <div className="flex flex-col gap-4">
          {groups
            .filter((g) => g.tasks.length)
            .map((group) => (
              <Card key={group.title}>
                <h2
                  className={cn('border-b px-4 py-2.5 text-xs font-semibold', group.tone === 'danger' && 'text-danger')}
                >
                  {group.title} <span className="font-normal text-muted">{group.tasks.length}</span>
                </h2>
                <ul className="divide-y">
                  {group.tasks.map((task) => (
                    <TaskRow
                      key={task.itemId}
                      task={task}
                      today={today}
                      onOpen={() => setSearch({ open: `${task.workspaceId}/${task.listId}/${task.itemId}` })}
                    />
                  ))}
                </ul>
              </Card>
            ))}
        </div>
      ) : (
        <Card>
          <EmptyState icon={CircleCheckBig} title="Nothing to do here" />
        </Card>
      )}
      {openWs && openList && openItem && (
        <ItemPanelLoader
          workspaceId={openWs}
          listId={openList}
          itemId={openItem}
          tab={search.tab ?? 'details'}
          onTab={(tab) => setSearch({ tab }, true)}
          onClose={() => setSearch({ open: undefined, tab: undefined })}
        />
      )}
    </Page>
  );
}

function TaskRow({ task, today, onOpen }: { task: MyTask; today: string; onOpen: () => void }) {
  const format = useFormat();
  const queryClient = useQueryClient();
  // Checked at once (local state keeps the click); the row leaves the list when the save is done.
  const [checked, setChecked] = useState(false);
  const complete = useMutation({
    mutationFn: async () => {
      const items = listBuilder(task.workspaceId!, task.listId!).items;
      const current = await items.byItemId(task.itemId!).get();
      await items.byItemId(task.itemId!).patch({ fields: fieldValues({ status: 'completed' }) }, ifMatch(current));
    },
    onSuccess: async () => {
      toast.success(`“${task.title}” done.`);
      await queryClient.invalidateQueries({ queryKey: ['me', 'tasks'] });
    },
    onError: () => setChecked(false),
  });
  const due = task.dueDate ?? undefined;
  return (
    <li className="flex items-center gap-3 px-4 py-2.5 hover:bg-surface-muted/50">
      <Checkbox
        aria-label={`Complete ${task.title}`}
        checked={checked}
        disabled={checked}
        onChange={() => {
          setChecked(true);
          complete.mutate();
        }}
      />
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 text-left">
        <span className="block truncate text-[13px] font-medium">{task.title}</span>
        <span className="text-xs text-muted">
          {task.listName}
          {task.status && task.status !== 'notStarted' && <> · {choiceLabel(task.status)}</>}
        </span>
      </button>
      {task.priority === 'high' && <Badge tone="danger">High</Badge>}
      {due && (
        <Badge tone={due < today ? 'danger' : due === today ? 'warning' : 'neutral'}>
          {due === today ? 'Today' : format.date(due)}
        </Badge>
      )}
    </li>
  );
}

/** Adds a task to a chosen task list, assigned to the user (the list is remembered). */
function QuickAdd() {
  const queryClient = useQueryClient();
  const { data: me } = useQuery(meQuery);
  const lists = useListsByTemplate('tasks');
  const [listId, setListId] = useState(() => rememberedList('quickTaskList') ?? '');
  const [title, setTitle] = useState('');
  const [due, setDue] = useState('');
  const target = lists.find((l) => l.id === listId) ?? lists[0];
  const add = useMutation({
    mutationFn: () =>
      listBuilder(target!.workspaceId!, target!.id!).items.post({
        fields: fieldValues({
          title: title.trim(),
          dueDate: due || undefined,
          assignedTo: me?.id ? [me.id] : undefined,
        }),
      }),
    onSuccess: async () => {
      rememberList('quickTaskList', target!.id!);
      setTitle('');
      setDue('');
      await queryClient.invalidateQueries({ queryKey: ['me', 'tasks'] });
    },
  });
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (title.trim() && target) add.mutate();
  };
  if (!lists.length) return null;
  return (
    <form onSubmit={onSubmit} className="mb-5 flex flex-wrap gap-2 rounded-lg border bg-surface p-2 shadow-xs">
      <Input
        aria-label="New task"
        placeholder="Add a task for me…"
        className="min-w-48 flex-1 border-0 shadow-none"
        value={title}
        onChange={(e) => setTitle(e.target.value)}
      />
      <Input aria-label="Due date" type="date" className="w-40" value={due} onChange={(e) => setDue(e.target.value)} />
      <Select
        aria-label="Task list"
        className="w-48"
        value={target?.id ?? ''}
        onChange={(e) => setListId(e.target.value)}
      >
        {lists.map((l) => (
          <option key={l.id} value={l.id!}>
            {l.workspaceName} › {l.name}
          </option>
        ))}
      </Select>
      <Button type="submit" variant="primary" disabled={!title.trim() || add.isPending}>
        <Plus /> Add
      </Button>
    </form>
  );
}
