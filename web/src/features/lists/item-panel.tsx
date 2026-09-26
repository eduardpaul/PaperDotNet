import type { ListResponse } from '@paperdotnet/client';
import { fieldsOf, ifMatch } from '@paperdotnet/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { FolderInput, FolderSearch, MoreHorizontal, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { keys } from '@/api/keys';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/feedback';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/menu';
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { itemPanels, type ItemPanelContext } from '@/extensibility/item-panels';
import { FollowButton } from '@/features/collaboration/follow-button';
import { MoveDocumentDialog } from '@/features/documents/move-dialog';
import { AddToFolderDialog } from '@/features/smart-folders/add-dialog';
import { useFormat } from '@/lib/preferences';
import { ItemForm } from './item-form';
import { itemQuery, listBuilder } from './queries';
import { contentTypeOf } from './schema';

/**
 * The item panel next to a list (frontend.md): details and the other tabs of an item, or a new item. Its state is in
 * the URL (?item=…&tab=…), so it survives reloads and can be shared.
 */
export function ItemPanel({
  workspaceId,
  list,
  itemId,
  parentId,
  initialValues,
  tab,
  onTab,
  onClose,
  onCreated,
}: {
  workspaceId: string;
  list: ListResponse;
  itemId: string;
  parentId?: string;
  initialValues?: Record<string, unknown>;
  tab: string;
  onTab: (tab: string) => void;
  onClose: () => void;
  onCreated: (itemId: string) => void;
}) {
  const isNew = itemId === 'new';
  const [moving, setMoving] = useState(false);
  const [classifying, setClassifying] = useState(false);
  const format = useFormat();
  const queryClient = useQueryClient();
  const { data: item, isPending } = useQuery({ ...itemQuery(workspaceId, list.id!, itemId), enabled: !isNew });
  const remove = useMutation({
    mutationFn: () => listBuilder(workspaceId, list.id!).items.byItemId(itemId).delete(ifMatch(item)),
    onSuccess: async () => {
      toast.success('Moved to the recycle bin.');
      onClose();
      // Items and the recycle bin.
      await queryClient.invalidateQueries({ queryKey: keys.list(workspaceId, list.id!) });
    },
  });
  const title = isNew
    ? `New ${contentTypeOf(list, undefined)?.name?.toLowerCase() ?? 'item'}`
    : String(fieldsOf(item).title ?? '');
  const context: ItemPanelContext | undefined = item ? { workspaceId, list, item } : undefined;
  const panels = context ? itemPanels.filter((p) => p.applies(context)) : [];

  return (
    <Sheet open onOpenChange={(open) => !open && onClose()}>
      <SheetContent aria-describedby={undefined}>
        <header className="flex items-start gap-2 border-b px-5 py-3.5">
          <div className="min-w-0 flex-1">
            <SheetTitle className="truncate text-base font-semibold">
              {isPending && !isNew ? '…' : title || 'Untitled'}
            </SheetTitle>
            {item && (
              <SheetDescription className="text-xs text-muted">
                {contentTypeOf(list, item.contentTypeId)?.name} · updated {format.relative(item.updatedAt)}
              </SheetDescription>
            )}
          </div>
          {item && !item.isFolder && <FollowButton workspaceId={workspaceId} listId={list.id!} item={item} />}
          {item && (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label="Item actions">
                  <MoreHorizontal />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                {list.kind === 'library' && !item.isFolder && (
                  <DropdownMenuItem onSelect={() => setMoving(true)}>
                    <FolderInput /> Move to a library…
                  </DropdownMenuItem>
                )}
                <DropdownMenuItem onSelect={() => setClassifying(true)}>
                  <FolderSearch /> Add to smart folder…
                </DropdownMenuItem>
                <DropdownMenuItem tone="danger" onSelect={() => remove.mutate()}>
                  <Trash2 /> Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          )}
          <SheetClose />
        </header>
        {isNew ? (
          <ItemForm
            workspaceId={workspaceId}
            list={list}
            parentId={parentId}
            initialValues={initialValues}
            onSaved={(saved) => onCreated(saved.id!)}
            onCancel={onClose}
          />
        ) : !item ? (
          <div className="space-y-4 p-5">
            <Skeleton className="h-9" />
            <Skeleton className="h-9" />
            <Skeleton className="h-9" />
          </div>
        ) : (
          <Tabs value={tab} onValueChange={onTab} className="flex min-h-0 flex-1 flex-col">
            <TabsList>
              <TabsTrigger value="details">Details</TabsTrigger>
              {panels.map((panel) => (
                <TabsTrigger key={panel.key} value={panel.key}>
                  {panel.label}
                </TabsTrigger>
              ))}
            </TabsList>
            <TabsContent value="details" className="flex min-h-0 flex-1 flex-col">
              <ItemForm
                key={item.id}
                workspaceId={workspaceId}
                list={list}
                item={item}
                onSaved={() => {}}
                onCancel={onClose}
              />
            </TabsContent>
            {panels.map((panel) => (
              <TabsContent key={panel.key} value={panel.key} className="min-h-0 flex-1 overflow-y-auto">
                <panel.component {...context!} />
              </TabsContent>
            ))}
          </Tabs>
        )}
        {item && classifying && (
          <AddToFolderDialog
            workspaceId={workspaceId}
            listId={list.id!}
            item={item}
            open
            onOpenChange={setClassifying}
          />
        )}
        {item && moving && (
          <MoveDocumentDialog
            workspaceId={workspaceId}
            list={list}
            item={item}
            open
            onOpenChange={setMoving}
            onMoved={onClose}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}
