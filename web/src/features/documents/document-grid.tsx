import type { ItemResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import { Folder } from 'lucide-react';
import { useFormat } from '@/lib/preferences';
import { Thumbnail } from './thumbnail';

/** Documents as thumbnails (LST-09 gallery layout), so scans are recognized at a glance (DOC-04). */
export function DocumentGrid({
  workspaceId,
  listId,
  items,
  onOpen,
}: {
  workspaceId: string;
  listId: string;
  items: ItemResponse[];
  onOpen: (item: ItemResponse) => void;
}) {
  const format = useFormat();
  return (
    <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-6">
      {items.map((item) => (
        <li key={item.id}>
          <button
            type="button"
            onClick={() => onOpen(item)}
            className="group flex w-full flex-col overflow-hidden rounded-lg border bg-surface text-left shadow-xs hover:border-accent/50"
          >
            {item.isFolder ? (
              <span className="flex aspect-[1/1.1] items-center justify-center bg-accent-soft/40">
                <Folder className="size-12 fill-current/20 text-accent" />
              </span>
            ) : (
              <Thumbnail
                workspaceId={workspaceId}
                listId={listId}
                itemId={item.id!}
                version={item.odataEtag}
                className="aspect-[1/1.1] rounded-none border-0 border-b"
              />
            )}
            <span className="flex flex-col gap-0.5 p-2.5">
              <span className="truncate text-[13px] font-medium">{String(fieldsOf(item).title ?? 'Untitled')}</span>
              <span className="text-xs text-muted">{format.relative(item.updatedAt)}</span>
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}
