import type { ItemResponse, ViewResponse } from '@paperdotnet/client';
import { fields as fieldValues, fieldsOf, ifMatch } from '@paperdotnet/client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import type { RowSelectionState } from '@tanstack/react-table';
import {
  FolderPlus,
  Inbox,
  LayoutGrid,
  List as ListLayout,
  Pencil,
  Plus,
  Search,
  Settings,
  Trash2,
  Upload,
  X,
} from 'lucide-react';
import { useDeferredValue, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Page, PageHeader } from '@/components/page';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton, Spinner } from '@/components/ui/feedback';
import { Input } from '@/components/ui/input';
import { DropZone, FilePickerButton } from '@/features/documents/drop-zone';
import { DocumentGrid } from '@/features/documents/document-grid';
import { acceptedTypes } from '@/features/documents/paths';
import { useUploads } from '@/features/documents/uploads';
import { ValueNamesProvider } from '@/features/fields/lookups';
import { BulkEditDialog } from '@/features/lists/bulk-edit-dialog';
import { FolderBreadcrumb } from '@/features/lists/folder-breadcrumb';
import { ItemPanel } from '@/features/lists/item-panel';
import { ItemsBoard } from '@/features/lists/items-board';
import { ItemsTable, orderByOf, type Sort } from '@/features/lists/items-table';
import { ListIcon } from '@/features/lists/list-icon';
import { NameDialog } from '@/features/lists/name-dialog';
import { listPermissionsQuery } from '@/features/list-settings/queries';
import { itemsQuery, listBuilder, listQuery, odataString, viewsQuery } from '@/features/lists/queries';
import { listFields } from '@/features/lists/schema';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

interface ListSearch {
  view?: string;
  q?: string;
  /** A field name, "-" first for descending. */
  sort?: string;
  folder?: string;
  item?: string;
  tab?: string;
  /** Libraries: grid of thumbnails instead of the table. */
  layout?: 'grid';
  /** Documents: the page to show in the preview (search page hits). */
  page?: number;
}

const text = (value: unknown) => (typeof value === 'string' && value ? value : undefined);

export const Route = createFileRoute('/_app/w/$workspaceId/l/$listId/')({
  validateSearch: (search: Record<string, unknown>): ListSearch => ({
    view: text(search.view),
    q: text(search.q),
    sort: text(search.sort),
    folder: text(search.folder),
    item: text(search.item),
    tab: text(search.tab),
    layout: search.layout === 'grid' ? 'grid' : undefined,
    page: Number.isInteger(Number(search.page)) && Number(search.page) > 0 ? Number(search.page) : undefined,
  }),
  loader: ({ context, params }) => context.queryClient.ensureQueryData(listQuery(params.workspaceId, params.listId)),
  component: ListPage,
});

function ListPage() {
  const { workspaceId, listId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  const { data: views } = useQuery(viewsQuery(workspaceId, listId));
  const { data: permissions } = useQuery(listPermissionsQuery(workspaceId, listId));
  const [query, setQuery] = useState(search.q ?? '');
  const deferredQuery = useDeferredValue(query.trim());
  const [selection, setSelection] = useState<RowSelectionState>({});
  const [bulkEdit, setBulkEdit] = useState(false);
  const [newFolder, setNewFolder] = useState(false);
  const { upload } = useUploads();

  const setSearch = (patch: Partial<ListSearch>, replace = false) =>
    void navigate({ search: (current) => ({ ...current, ...patch }), replace });

  const view: ViewResponse | undefined =
    views?.find((v) => v.id === search.view) ?? views?.find((v) => v.isDefault) ?? views?.[0];
  const allFields = useMemo(() => listFields(list), [list]);
  const columns = useMemo(() => {
    if (!view?.columns?.length) return allFields.slice(0, 6);
    const named = view.columns.map((name) => allFields.find((f) => f.name === name)).filter((f) => f !== undefined);
    return named[0]?.name === 'title' ? named : [allFields[0]!, ...named.filter((f) => f.name !== 'title')];
  }, [view, allFields]);
  const sort: Sort | undefined = search.sort
    ? { field: search.sort.replace(/^-/, ''), descending: search.sort.startsWith('-') }
    : undefined;

  const filter = [
    view?.filter ? `(${view.filter})` : undefined,
    list?.allowFolders && !deferredQuery
      ? search.folder
        ? `parentId eq ${search.folder}`
        : 'parentId eq null'
      : undefined,
    deferredQuery ? `contains(tolower(fields/title),${odataString(deferredQuery.toLowerCase())})` : undefined,
  ]
    .filter(Boolean)
    .join(' and ');
  const orderby = [list?.allowFolders ? 'isFolder desc' : undefined, orderByOf(sort) ?? view?.orderBy ?? undefined]
    .filter(Boolean)
    .join(',');
  const items = useInfiniteQuery({
    ...itemsQuery(workspaceId, listId, { filter, orderby }),
    enabled: !!list && views !== undefined,
  });
  const rows = useMemo(() => items.data?.pages.flatMap((p) => p.value ?? []) ?? [], [items.data]);
  const total = items.data?.pages[0]?.odataCount;
  const selectedIds = Object.keys(selection).filter((id) => selection[id]);

  const groupBy =
    view?.layout === 'board' ? allFields.find((f) => f.name === view.groupBy && f.type === 'choice') : undefined;

  const removeSelected = useMutation({
    mutationFn: async () => {
      for (const item of rows.filter((r) => selection[r.id!])) {
        await listBuilder(workspaceId, listId).items.byItemId(item.id!).delete(ifMatch(item));
      }
    },
    onSettled: async () => {
      setSelection({});
      // Items and the recycle bin.
      await queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, listId) });
    },
    onSuccess: () => toast.success('Moved to the recycle bin.'),
  });
  const createFolder = useMutation({
    meta: { silent: true },
    mutationFn: (name: string) =>
      listBuilder(workspaceId, listId).items.post({
        isFolder: true,
        parentId: search.folder,
        fields: fieldValues({ title: name }),
      }),
    onSuccess: async () => {
      setNewFolder(false);
      await queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, listId) });
    },
  });

  const open = (item: ItemResponse) => setSearch({ item: item.id!, tab: undefined });

  if (!list) return null;
  const isLibrary = list.kind === 'library';
  const uploadHere = (files: File[]) =>
    upload(files, { kind: 'library', workspaceId, listId, folderId: search.folder, name: list.name ?? 'library' });

  return (
    <Page wide className="max-w-[1600px]">
      <PageHeader
        title={
          <span className="flex items-center gap-2">
            <ListIcon list={list} className="size-5 text-accent" />
            {list.name}
          </span>
        }
        description={list.description}
        actions={
          <>
            {permissions?.effectiveLevel === 'manage' && (
              <Button asChild variant="ghost" size="icon" aria-label="List settings">
                <Link to="/w/$workspaceId/l/$listId/settings" params={{ workspaceId, listId }}>
                  <Settings />
                </Link>
              </Button>
            )}
            {list.allowFolders && (
              <Button onClick={() => setNewFolder(true)}>
                <FolderPlus /> New folder
              </Button>
            )}
            {isLibrary ? (
              <FilePickerButton variant="primary" accept={acceptedTypes} onFiles={uploadHere}>
                <Upload /> Upload
              </FilePickerButton>
            ) : (
              <Button variant="primary" onClick={() => setSearch({ item: 'new', tab: undefined })}>
                <Plus /> New {list.contentTypes?.length === 1 ? list.contentTypes[0]!.name?.toLowerCase() : 'item'}
              </Button>
            )}
          </>
        }
      />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        {(views?.length ?? 0) > 1 && (
          <div role="tablist" aria-label="Views" className="flex rounded-md bg-surface-muted p-0.5">
            {views!.map((v) => (
              <button
                key={v.id}
                role="tab"
                type="button"
                aria-selected={v.id === view?.id}
                onClick={() => {
                  setSelection({});
                  setSearch({ view: v.id!, sort: undefined });
                }}
                className={cn(
                  'rounded px-2.5 py-1 text-xs font-medium text-muted',
                  v.id === view?.id && 'bg-surface text-foreground shadow-xs',
                )}
              >
                {v.name}
              </button>
            ))}
          </div>
        )}
        {isLibrary && !groupBy && (
          <div role="group" aria-label="Layout" className="ml-auto flex rounded-md bg-surface-muted p-0.5">
            <button
              type="button"
              aria-pressed={!search.layout}
              aria-label="Table"
              onClick={() => setSearch({ layout: undefined })}
              className={cn('rounded p-1 text-muted', !search.layout && 'bg-surface text-foreground shadow-xs')}
            >
              <ListLayout className="size-4" />
            </button>
            <button
              type="button"
              aria-pressed={search.layout === 'grid'}
              aria-label="Thumbnails"
              onClick={() => setSearch({ layout: 'grid' })}
              className={cn(
                'rounded p-1 text-muted',
                search.layout === 'grid' && 'bg-surface text-foreground shadow-xs',
              )}
            >
              <LayoutGrid className="size-4" />
            </button>
          </div>
        )}
        <div className={cn('relative w-full max-w-xs', !(isLibrary && !groupBy) && 'ml-auto')}>
          <Search className="absolute top-2.5 left-2.5 size-4 text-muted" />
          <Input
            aria-label="Search this list"
            placeholder="Search titles…"
            className="pl-8"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setSearch({ q: e.target.value || undefined }, true);
            }}
          />
        </div>
        <Link
          to="/w/$workspaceId/l/$listId/recycle-bin"
          params={{ workspaceId, listId }}
          className="inline-flex items-center gap-1 text-xs text-muted hover:text-foreground"
        >
          <Trash2 className="size-3.5" /> Recycle bin
        </Link>
      </div>

      {search.folder && (
        <FolderBreadcrumb
          workspaceId={workspaceId}
          listId={listId}
          listName={list.name ?? ''}
          folderId={search.folder}
          onNavigate={(folder) => setSearch({ folder })}
        />
      )}

      {selectedIds.length > 0 && (
        <div className="mb-3 flex flex-wrap items-center gap-2 rounded-lg bg-accent-soft px-3 py-2 text-[13px]">
          <span className="font-medium">{selectedIds.length} selected</span>
          <Button size="sm" onClick={() => setBulkEdit(true)}>
            <Pencil /> Edit field
          </Button>
          <Button size="sm" disabled={removeSelected.isPending} onClick={() => removeSelected.mutate()}>
            <Trash2 /> Delete
          </Button>
          <Button size="sm" variant="ghost" className="ml-auto" onClick={() => setSelection({})}>
            <X /> Clear
          </Button>
        </div>
      )}

      <DropZone
        onFiles={uploadHere}
        disabled={!isLibrary}
        label={`Drop to upload to ${list.name}`}
        className="min-h-40"
      >
        <ValueNamesProvider fields={columns} values={rows.map((r) => fieldsOf(r))}>
          {items.isPending ? (
            <div className="space-y-2">
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
            </div>
          ) : items.isError ? (
            <Card>
              <EmptyState title="The items could not be loaded">{problemMessage(items.error)}</EmptyState>
            </Card>
          ) : rows.length === 0 ? (
            <Card>
              <EmptyState
                icon={Inbox}
                title={deferredQuery ? 'No matching items' : search.folder ? 'This folder is empty' : 'No items yet'}
              >
                {!deferredQuery && (
                  <Button variant="primary" className="mt-2" onClick={() => setSearch({ item: 'new' })}>
                    <Plus /> Add the first one
                  </Button>
                )}
              </EmptyState>
            </Card>
          ) : isLibrary && search.layout === 'grid' ? (
            <DocumentGrid
              workspaceId={workspaceId}
              listId={listId}
              items={rows}
              onOpen={(item) =>
                item.isFolder ? setSearch({ folder: item.id! }) : setSearch({ item: item.id!, tab: 'preview' })
              }
            />
          ) : groupBy ? (
            <ItemsBoard
              workspaceId={workspaceId}
              list={list}
              items={rows}
              groupBy={groupBy}
              cardFields={columns}
              onOpen={open}
            />
          ) : (
            <ItemsTable
              items={rows}
              fields={columns}
              sort={sort}
              onSort={(next) => setSearch({ sort: next ? `${next.descending ? '-' : ''}${next.field}` : undefined })}
              selection={selection}
              onSelectionChange={setSelection}
              onOpen={open}
              onOpenFolder={(folder) => setSearch({ folder: folder.id! })}
              activeId={search.item}
              thumbnails={isLibrary ? { workspaceId, listId } : undefined}
              source={{ workspaceId, listId }}
            />
          )}
        </ValueNamesProvider>
      </DropZone>

      <div className="mt-3 flex items-center gap-3 text-xs text-muted">
        {total !== undefined && total !== null && (
          <span>
            {rows.length} of {total}
          </span>
        )}
        {items.hasNextPage && (
          <Button size="sm" disabled={items.isFetchingNextPage} onClick={() => void items.fetchNextPage()}>
            {items.isFetchingNextPage && <Spinner />} Load more
          </Button>
        )}
      </div>

      {search.item && (
        <ItemPanel
          workspaceId={workspaceId}
          list={list}
          itemId={search.item}
          parentId={search.folder}
          tab={search.tab ?? 'details'}
          onTab={(tab) => setSearch({ tab: tab === 'details' ? undefined : tab }, true)}
          onClose={() => setSearch({ item: undefined, tab: undefined, page: undefined })}
          onCreated={(id) => setSearch({ item: id }, true)}
        />
      )}
      <BulkEditDialog
        workspaceId={workspaceId}
        list={list}
        ids={selectedIds}
        open={bulkEdit}
        onOpenChange={setBulkEdit}
        onDone={() => setSelection({})}
      />
      {newFolder && (
        <NameDialog
          open
          onOpenChange={setNewFolder}
          title="New folder"
          submit="Create folder"
          busy={createFolder.isPending}
          error={createFolder.isError ? problemMessage(createFolder.error) : undefined}
          onSubmit={(name) => createFolder.mutate(name)}
        />
      )}
    </Page>
  );
}
