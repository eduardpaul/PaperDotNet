import type { SmartFolderEntry } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { ChevronRight, FolderSearch, Home, MinusCircle, MoreHorizontal, Pencil, Sparkles, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { Page, PageHeader } from '@/components/page';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { EmptyState, Skeleton, Spinner } from '@/components/ui/feedback';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/menu';
import { ItemPanelLoader } from '@/features/lists/item-panel-loader';
import { SmartFolderDialog } from '@/features/smart-folders/folder-dialog';
import { folderItemsQuery, groupsQuery, smartFolderQuery } from '@/features/smart-folders/queries';
import { useFormat } from '@/lib/preferences';

interface FolderSearch {
  path?: string[];
  /** The open item: "workspaceId/listId/itemId". */
  open?: string;
  tab?: string;
}

export const Route = createFileRoute('/_app/f/$folderId')({
  validateSearch: (search: Record<string, unknown>): FolderSearch => ({
    path: Array.isArray(search.path) ? search.path.map(String) : undefined,
    open: typeof search.open === 'string' && search.open.split('/').length === 3 ? search.open : undefined,
    tab: typeof search.tab === 'string' ? search.tab : undefined,
  }),
  component: SmartFolder,
});

/** A smart folder (TAX-08): its virtual sub-folders (TAX-10) and matching items from every list. */
function SmartFolder() {
  const { folderId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const { data: folder } = useQuery(smartFolderQuery(folderId));
  const path = search.path ?? [];
  const levels = folder?.definition?.groupBy ?? [];
  const showGroups = path.length < levels.length;
  const groups = useQuery({ ...groupsQuery(folderId, path), enabled: !!folder && showGroups });
  const items = useInfiniteQuery({ ...folderItemsQuery(folderId, path), enabled: !!folder && !showGroups });
  const entries = items.data?.pages.flatMap((p) => p.value ?? []) ?? [];
  const setSearch = (patch: Partial<FolderSearch>, replace = false) =>
    void navigate({ search: (c) => ({ ...c, ...patch }), replace });
  const [openWs, openList, openItem] = search.open?.split('/') ?? [];

  const remove = useMutation({
    mutationFn: () => api.v10.smartFolders.byId(folderId).delete(),
    onSuccess: async () => {
      toast.success('Smart folder deleted.');
      await queryClient.invalidateQueries({ queryKey: keys.smartFolders });
      await navigate({ to: '/' });
    },
  });

  if (!folder) return null;

  return (
    <Page wide className="max-w-[1400px]">
      <PageHeader
        icon={FolderSearch}
        title={
          <span className="flex items-center gap-2">
            {folder.name}
            <Badge>{folder.personal ? 'Personal' : 'Shared'}</Badge>
          </span>
        }
        description={folder.description}
        actions={
          <>
            <Button onClick={() => setEditing(true)}>
              <Pencil /> Edit
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button size="icon" aria-label="Folder actions">
                  <MoreHorizontal />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem tone="danger" onSelect={() => remove.mutate()}>
                  <Trash2 /> Delete folder
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </>
        }
      />
      {levels.length > 0 && (
        <nav aria-label="Sub-folder" className="mb-4 flex flex-wrap items-center gap-1 text-[13px] text-muted">
          <button
            type="button"
            className="inline-flex items-center gap-1 hover:text-foreground"
            onClick={() => setSearch({ path: undefined })}
          >
            <Home className="size-3.5" /> {folder.name}
          </button>
          {path.map((value, index) => (
            <span key={index} className="inline-flex items-center gap-1">
              <ChevronRight className="size-3.5" />
              <button
                type="button"
                className={index === path.length - 1 ? 'font-medium text-foreground' : 'hover:text-foreground'}
                onClick={() => setSearch({ path: path.slice(0, index + 1) })}
              >
                {value || '(empty)'}
              </button>
            </span>
          ))}
        </nav>
      )}

      {showGroups ? (
        groups.isPending ? (
          <Skeleton className="h-32" />
        ) : groups.data?.value?.length ? (
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {groups.data.value.map((group) => (
              <li key={group.value ?? '(empty)'}>
                <button
                  type="button"
                  onClick={() => setSearch({ path: [...path, group.value ?? ''] })}
                  className="flex w-full items-center gap-3 rounded-lg border bg-surface p-4 text-left shadow-xs hover:border-accent/50"
                >
                  <FolderSearch className="size-5 shrink-0 text-accent" />
                  <span className="min-w-0 flex-1 truncate font-medium">{group.label || '(empty)'}</span>
                  <span className="text-xs text-muted tabular-nums">{group.count}</span>
                </button>
              </li>
            ))}
          </ul>
        ) : (
          <Card>
            <EmptyState icon={Sparkles} title="Nothing matches yet" />
          </Card>
        )
      ) : items.isPending ? (
        <Skeleton className="h-32" />
      ) : entries.length ? (
        <>
          <Card className="overflow-hidden">
            <ul className="divide-y">
              {entries.map((entry) => (
                <EntryRow
                  key={entry.item?.id}
                  folderId={folderId}
                  entry={entry}
                  onOpen={() => setSearch({ open: `${entry.workspaceId}/${entry.item?.listId}/${entry.item?.id}` })}
                />
              ))}
            </ul>
          </Card>
          {items.hasNextPage && (
            <Button
              className="mt-3"
              size="sm"
              disabled={items.isFetchingNextPage}
              onClick={() => void items.fetchNextPage()}
            >
              {items.isFetchingNextPage && <Spinner />} Load more
            </Button>
          )}
        </>
      ) : (
        <Card>
          <EmptyState icon={Sparkles} title="Nothing matches yet">
            Drag items from a list onto this folder in the sidebar, or use “Add to smart folder” in an item’s menu.
          </EmptyState>
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
      {editing && <SmartFolderDialog folder={folder} open onOpenChange={setEditing} />}
    </Page>
  );
}

function EntryRow({ folderId, entry, onOpen }: { folderId: string; entry: SmartFolderEntry; onOpen: () => void }) {
  const format = useFormat();
  const queryClient = useQueryClient();
  const item = entry.item!;
  const takeOut = useMutation({
    mutationFn: () =>
      api.v10.smartFolders
        .byId(folderId)
        .items.byItemId(item.id!)
        .delete({ queryParameters: { workspaceId: entry.workspaceId!, listId: item.listId! } }),
    onSuccess: async () => {
      toast.success('Taken out of the folder.');
      await queryClient.invalidateQueries({ queryKey: [...keys.smartFolders, folderId] });
    },
  });
  return (
    <li className="group flex items-center gap-3 px-4 py-2.5">
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 text-left">
        <span className="block truncate text-[13px] font-medium">{String(fieldsOf(item).title ?? 'Untitled')}</span>
        <span className="text-xs text-muted">
          {entry.listName} · {format.relative(item.updatedAt)}
        </span>
      </button>
      <Button
        size="sm"
        variant="ghost"
        className="opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
        disabled={takeOut.isPending}
        onClick={() => takeOut.mutate()}
      >
        <MinusCircle /> Take out
      </Button>
    </li>
  );
}
