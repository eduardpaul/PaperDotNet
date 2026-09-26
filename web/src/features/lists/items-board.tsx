import type { ItemResponse, ListResponse } from '@paperdotnet/client';
import { fields as fieldValues, fieldsOf, ifMatch } from '@paperdotnet/client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { MoreHorizontal } from 'lucide-react';
import { useState } from 'react';
import { keys } from '@/api/keys';
import { FieldValue } from '@/features/fields/display';
import { choiceLabel, type FieldDefinition } from '@/features/fields/values';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/menu';
import { cn } from '@/lib/utils';
import { listBuilder } from './queries';

const None = '';

/**
 * A board (LST-09, TSK-04): a column per choice of the group-by field. Cards move by drag and drop or, from the
 * keyboard, with their "Move to" menu; either way the item is saved with If-Match.
 */
export function ItemsBoard({
  workspaceId,
  list,
  items,
  groupBy,
  cardFields,
  onOpen,
}: {
  workspaceId: string;
  list: ListResponse;
  items: ItemResponse[];
  groupBy: FieldDefinition;
  cardFields: FieldDefinition[];
  onOpen: (item: ItemResponse) => void;
}) {
  const queryClient = useQueryClient();
  const [dragOver, setDragOver] = useState<string>();
  const valueOf = (item: ItemResponse) => String(fieldsOf(item)[groupBy.name!] ?? None);
  // "No value" only while some items have none.
  const columns = [...(groupBy.choices ?? []), ...(items.some((i) => valueOf(i) === None) ? [None] : [])];
  const move = useMutation({
    mutationFn: ({ item, value }: { item: ItemResponse; value: string }) =>
      listBuilder(workspaceId, list.id!)
        .items.byItemId(item.id!)
        .patch({ fields: fieldValues({ [groupBy.name!]: value || null }) }, ifMatch(item)),
    onSettled: () => queryClient.invalidateQueries({ queryKey: keys.items(workspaceId, list.id!) }),
  });
  return (
    <div className="flex gap-3 overflow-x-auto pb-2">
      {columns.map((column) => {
        const cards = items.filter((item) => valueOf(item) === column);
        return (
          <section
            key={column || 'none'}
            aria-label={column ? choiceLabel(column) : 'No value'}
            className={cn(
              'flex w-72 shrink-0 flex-col rounded-lg border bg-surface-muted/60',
              dragOver === column && 'border-accent bg-accent-soft/40',
            )}
            onDragOver={(e) => {
              e.preventDefault();
              setDragOver(column);
            }}
            onDragLeave={() => setDragOver(undefined)}
            onDrop={(e) => {
              e.preventDefault();
              setDragOver(undefined);
              const item = items.find((i) => i.id === e.dataTransfer.getData('text/plain'));
              if (item && valueOf(item) !== column) move.mutate({ item, value: column });
            }}
          >
            <h3 className="flex items-center gap-2 px-3 py-2.5 text-xs font-semibold">
              {column ? choiceLabel(column) : 'No value'}
              <span className="font-normal text-muted">{cards.length}</span>
            </h3>
            <ul className="flex min-h-16 flex-col gap-2 px-2 pb-2">
              {cards.map((item) => (
                <li
                  key={item.id}
                  draggable
                  onDragStart={(e) => e.dataTransfer.setData('text/plain', item.id!)}
                  className="group rounded-md border bg-surface p-2.5 shadow-xs"
                >
                  <div className="flex items-start gap-1">
                    <button
                      type="button"
                      className="min-w-0 flex-1 text-left text-[13px] font-medium"
                      onClick={() => onOpen(item)}
                    >
                      {String(fieldsOf(item).title ?? 'Untitled')}
                    </button>
                    <DropdownMenu>
                      <DropdownMenuTrigger
                        className="rounded p-0.5 text-muted opacity-0 group-hover:opacity-100 hover:bg-surface-muted focus-visible:opacity-100"
                        aria-label="Move to"
                      >
                        <MoreHorizontal className="size-4" />
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuLabel>Move to</DropdownMenuLabel>
                        {columns
                          .filter((c) => c !== column)
                          .map((c) => (
                            <DropdownMenuItem key={c || 'none'} onSelect={() => move.mutate({ item, value: c })}>
                              {c ? choiceLabel(c) : 'No value'}
                            </DropdownMenuItem>
                          ))}
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </div>
                  <div className="mt-1.5 flex flex-col gap-1 text-xs">
                    {cardFields
                      .filter((f) => f.name !== 'title' && f.name !== groupBy.name && fieldsOf(item)[f.name!] != null)
                      .slice(0, 3)
                      .map((f) => (
                        <FieldValue key={f.name} field={f} value={fieldsOf(item)[f.name!]} />
                      ))}
                  </div>
                </li>
              ))}
            </ul>
          </section>
        );
      })}
    </div>
  );
}
