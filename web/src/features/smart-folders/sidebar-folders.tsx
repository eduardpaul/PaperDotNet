import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ChevronRight, FolderSearch, Plus } from 'lucide-react';
import { Collapsible } from 'radix-ui';
import { useState } from 'react';
import { toast } from 'sonner';
import { api } from '@/api/client';
import { keys } from '@/api/keys';
import { problemMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';
import { SmartFolderDialog } from './folder-dialog';
import { ItemDragType, smartFoldersQuery, type DraggedItem } from './queries';

/** Smart folders in the sidebar; dropping a list row on one classifies the item (TAX-09). */
export function SidebarSmartFolders({
  itemClass,
  activeClass,
  onNavigate,
}: {
  itemClass: string;
  activeClass: string;
  onNavigate?: () => void;
}) {
  const queryClient = useQueryClient();
  const { data } = useQuery(smartFoldersQuery);
  const [open, setOpen] = useState(true);
  const [creating, setCreating] = useState(false);
  const [over, setOver] = useState<string>();
  const drop = useMutation({
    mutationFn: ({ folderId, item }: { folderId: string; item: DraggedItem }) =>
      api.v10.smartFolders
        .byId(folderId)
        .items.post({ workspaceId: item.workspaceId, listId: item.listId, itemId: item.itemId }),
    onSuccess: async (_, { folderId, item }) => {
      toast.success(`“${item.title}” added to ${data?.find((f) => f.id === folderId)?.name}.`);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: [...keys.smartFolders, folderId] }),
        queryClient.invalidateQueries({ queryKey: keys.list(item.workspaceId, item.listId) }),
      ]);
    },
    onError: (error) => toast.error(problemMessage(error)),
    meta: { silent: true },
  });

  return (
    <Collapsible.Root open={open} onOpenChange={setOpen} className="mt-5">
      <div className="flex items-center px-2 pb-1">
        <Collapsible.Trigger className="flex flex-1 items-center gap-1 text-xs font-medium text-muted hover:text-foreground">
          <ChevronRight className={cn('size-3 transition-transform', open && 'rotate-90')} />
          Smart folders
        </Collapsible.Trigger>
        <button
          type="button"
          aria-label="New smart folder"
          onClick={() => setCreating(true)}
          className="rounded p-0.5 text-muted hover:bg-surface-muted hover:text-foreground"
        >
          <Plus className="size-3.5" />
        </button>
      </div>
      <Collapsible.Content>
        <ul className="flex flex-col gap-0.5">
          {data?.map((folder) => (
            <li
              key={folder.id}
              onDragOver={(e) => {
                if (!e.dataTransfer.types.includes(ItemDragType)) return;
                e.preventDefault();
                setOver(folder.id!);
              }}
              onDragLeave={() => setOver(undefined)}
              onDrop={(e) => {
                setOver(undefined);
                const raw = e.dataTransfer.getData(ItemDragType);
                if (!raw) return;
                e.preventDefault();
                drop.mutate({ folderId: folder.id!, item: JSON.parse(raw) as DraggedItem });
              }}
            >
              <Link
                to="/f/$folderId"
                params={{ folderId: folder.id! }}
                onClick={onNavigate}
                className={cn(itemClass, over === folder.id && 'bg-accent-soft ring-1 ring-accent')}
                activeProps={{ className: activeClass }}
              >
                <FolderSearch />
                <span className="truncate">{folder.name}</span>
              </Link>
            </li>
          ))}
          {data?.length === 0 && (
            <li className="px-2 py-1 text-xs text-muted">Folders by tags and rules, across lists.</li>
          )}
        </ul>
      </Collapsible.Content>
      {creating && <SmartFolderDialog open onOpenChange={setCreating} />}
    </Collapsible.Root>
  );
}
