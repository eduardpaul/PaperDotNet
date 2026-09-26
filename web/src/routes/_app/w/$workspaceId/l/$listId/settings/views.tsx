import type { ViewLayout, ViewResponse } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { ArrowDown, ArrowUp, LayoutList, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Alert, EmptyState, Skeleton } from '@/components/ui/feedback';
import { Input, Label } from '@/components/ui/input';
import { Checkbox, Select } from '@/components/ui/select';
import { useCanManageList } from '@/features/list-settings/queries';
import { listBuilder, listQuery, viewsQuery } from '@/features/lists/queries';
import { fieldLabel, listFields } from '@/features/lists/schema';
import { ConfirmDialog, SettingsSection } from '@/features/settings/section';
import { problemMessage } from '@/lib/errors';

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/settings/views')({ component: Views });

const layouts: { value: ViewLayout; label: string }[] = [
  { value: 'table', label: 'Table' },
  { value: 'board', label: 'Board (grouped by a choice)' },
];

/** Saved views (LST-09): columns, order, filter, grouping and layout; one is the default. */
function Views() {
  const { workspaceId, listId } = Route.useParams();
  const queryClient = useQueryClient();
  const { data: views, isPending } = useQuery(viewsQuery(workspaceId, listId));
  const canManage = useCanManageList(workspaceId, listId);
  const [editing, setEditing] = useState<ViewResponse | 'new'>();
  const [deleting, setDeleting] = useState<ViewResponse>();
  const remove = useMutation({
    meta: { silent: true },
    mutationFn: (view: ViewResponse) => listBuilder(workspaceId, listId).views.byViewId(view.id!).delete(),
    onSuccess: async () => {
      setDeleting(undefined);
      toast.success('View deleted.');
      await queryClient.invalidateQueries({ queryKey: viewsQuery(workspaceId, listId).queryKey });
    },
  });

  return (
    <>
      <SettingsSection
        title="Views"
        description="Ways to look at the list; everyone sees them as tabs above the items."
        className="px-0 pb-0"
        actions={
          canManage && (
            <Button variant="primary" onClick={() => setEditing('new')}>
              <Plus /> New view
            </Button>
          )
        }
      >
        {isPending ? (
          <Skeleton className="mx-5 mb-5 h-16" />
        ) : views?.length ? (
          <ul className="divide-y border-t">
            {views.map((view) => (
              <li key={view.id} className="flex items-center gap-3 px-5 py-3">
                <LayoutList className="size-4 text-muted" />
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 text-[13px] font-medium">
                    {view.name}
                    {view.isDefault && <Badge tone="success">Default</Badge>}
                    <Badge>{view.layout}</Badge>
                  </p>
                  <p className="truncate text-xs text-muted">
                    {view.columns?.length ? view.columns.join(', ') : 'Default columns'}
                    {view.filter && ` · filter: ${view.filter}`}
                    {view.orderBy && ` · sorted by ${view.orderBy}`}
                  </p>
                </div>
                <Button size="sm" variant="ghost" aria-label={`Edit ${view.name}`} onClick={() => setEditing(view)}>
                  <Pencil /> {canManage ? 'Edit' : 'View'}
                </Button>
                {canManage && (views.length ?? 0) > 1 && (
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`Delete ${view.name}`}
                    onClick={() => setDeleting(view)}
                  >
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState icon={LayoutList} title="No views" className="border-t py-8" />
        )}
      </SettingsSection>
      {editing && (
        <ViewDialog
          workspaceId={workspaceId}
          listId={listId}
          view={editing === 'new' ? undefined : editing}
          canManage={canManage}
          onClose={() => setEditing(undefined)}
        />
      )}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(undefined)}
        title={`Delete the view “${deleting?.name ?? ''}”?`}
        description="The items stay; only this way of looking at them goes."
        confirm="Delete"
        busy={remove.isPending}
        error={remove.error}
        onConfirm={() => deleting && remove.mutate(deleting)}
      />
    </>
  );
}

function ViewDialog({
  workspaceId,
  listId,
  view,
  canManage,
  onClose,
}: {
  workspaceId: string;
  listId: string;
  view?: ViewResponse;
  canManage: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  const fields = listFields(list);
  const [name, setName] = useState(view?.name ?? '');
  const [columns, setColumns] = useState<string[]>(
    view?.columns?.length ? view.columns : fields.slice(0, 5).map((f) => f.name!),
  );
  const [sortField, setSortField] = useState(view?.orderBy?.split(' ')[0]?.replace(/^fields\//, '') ?? '');
  const [sortDesc, setSortDesc] = useState(/ desc$/i.test(view?.orderBy ?? ''));
  const [filter, setFilter] = useState(view?.filter ?? '');
  const [layout, setLayout] = useState<ViewLayout>(view?.layout ?? 'table');
  const [groupBy, setGroupBy] = useState(view?.groupBy ?? '');
  const [isDefault, setIsDefault] = useState(!!view?.isDefault);
  const choiceFields = fields.filter((f) => f.type === 'choice' && !f.allowMultiple);
  const save = useMutation({
    meta: { silent: true },
    mutationFn: () => {
      const body = {
        name: name.trim(),
        columns,
        filter: filter.trim() || null,
        orderBy: sortField ? `${orderKey(sortField)}${sortDesc ? ' desc' : ''}` : null,
        groupBy: layout === 'board' ? groupBy || null : null,
        layout,
        isDefault,
      };
      const views = listBuilder(workspaceId, listId).views;
      return view ? views.byViewId(view.id!).put(body) : views.post(body);
    },
    onSuccess: async () => {
      toast.success(view ? 'View saved.' : 'View created.');
      await queryClient.invalidateQueries({ queryKey: viewsQuery(workspaceId, listId).queryKey });
      onClose();
    },
  });
  const move = (index: number, by: number) => {
    const next = [...columns];
    const [column] = next.splice(index, 1);
    next.splice(index + by, 0, column!);
    setColumns(next);
  };
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  const label = (name: string) => fieldLabel(fields.find((f) => f.name === name) ?? { name });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-xl">
        <form onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{view ? `View “${view.name}”` : 'New view'}</DialogTitle>
          </DialogHeader>
          <fieldset disabled={!canManage} className="flex max-h-[62vh] flex-col gap-4 overflow-y-auto px-5 pb-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="view-name">Name</Label>
                <Input id="view-name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="view-layout">Layout</Label>
                <Select id="view-layout" value={layout} onChange={(e) => setLayout(e.target.value as ViewLayout)}>
                  {layouts.map((l) => (
                    <option key={l.value} value={l.value} disabled={l.value === 'board' && !choiceFields.length}>
                      {l.label}
                    </option>
                  ))}
                  {!layouts.some((l) => l.value === layout) && <option value={layout}>{layout}</option>}
                </Select>
              </div>
            </div>
            {layout === 'board' && (
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="view-group">Columns of the board</Label>
                <Select id="view-group" required value={groupBy} onChange={(e) => setGroupBy(e.target.value)}>
                  <option value="">Choose a choice field…</option>
                  {choiceFields.map((f) => (
                    <option key={f.name} value={f.name!}>
                      {fieldLabel(f)}
                    </option>
                  ))}
                </Select>
              </div>
            )}
            <fieldset className="flex flex-col gap-1.5">
              <legend className="mb-1.5 text-[13px] font-medium">Columns</legend>
              <ul className="flex flex-col rounded-md border">
                {[...columns, ...fields.map((f) => f.name!).filter((n) => !columns.includes(n))].map((name) => {
                  const index = columns.indexOf(name);
                  const shown = index >= 0;
                  return (
                    <li key={name} className="flex items-center gap-2 border-b px-2 py-1 last:border-b-0">
                      <label className="flex flex-1 items-center gap-2 text-[13px]">
                        <Checkbox
                          checked={shown}
                          onChange={(e) =>
                            setColumns(e.target.checked ? [...columns, name] : columns.filter((c) => c !== name))
                          }
                        />
                        {label(name)}
                      </label>
                      {shown && (
                        <>
                          <Button
                            size="icon"
                            variant="ghost"
                            aria-label={`Move ${label(name)} up`}
                            disabled={index === 0}
                            onClick={() => move(index, -1)}
                          >
                            <ArrowUp />
                          </Button>
                          <Button
                            size="icon"
                            variant="ghost"
                            aria-label={`Move ${label(name)} down`}
                            disabled={index === columns.length - 1}
                            onClick={() => move(index, 1)}
                          >
                            <ArrowDown />
                          </Button>
                        </>
                      )}
                    </li>
                  );
                })}
              </ul>
            </fieldset>
            <div className="grid gap-3 sm:grid-cols-[1fr_auto]">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="view-sort">Sort by</Label>
                <Select id="view-sort" value={sortField} onChange={(e) => setSortField(e.target.value)}>
                  <option value="">Default (newest first)</option>
                  {['createdAt', 'updatedAt'].map((n) => (
                    <option key={n} value={n}>
                      {n === 'createdAt' ? 'Created' : 'Modified'}
                    </option>
                  ))}
                  {fields.map((f) => (
                    <option key={f.name} value={f.name!}>
                      {fieldLabel(f)}
                    </option>
                  ))}
                </Select>
              </div>
              <label className="flex items-center gap-2 self-end pb-2 text-[13px]">
                <Checkbox checked={sortDesc} disabled={!sortField} onChange={(e) => setSortDesc(e.target.checked)} />
                Descending
              </label>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="view-filter">Only items where</Label>
              <Input
                id="view-filter"
                className="font-mono text-xs"
                placeholder="fields/status ne 'completed'"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
              />
              <p className="text-xs text-muted">Optional. An OData filter, as in the items API.</p>
            </div>
            <label className="flex items-center gap-2 text-[13px]">
              <Checkbox checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} />
              Show this view first
            </label>
            {save.isError && <Alert>{problemMessage(save.error)}</Alert>}
          </fieldset>
          <DialogFooter>
            <Button type="button" onClick={onClose}>
              {canManage ? 'Cancel' : 'Close'}
            </Button>
            {canManage && (
              <Button type="submit" variant="primary" disabled={!name.trim() || save.isPending}>
                {view ? 'Save' : 'Create view'}
              </Button>
            )}
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** System columns sort by their own name; fields by fields/name. */
const orderKey = (name: string) => (['createdAt', 'updatedAt'].includes(name) ? name : `fields/${name}`);
