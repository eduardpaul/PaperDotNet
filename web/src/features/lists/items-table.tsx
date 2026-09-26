import type { ItemResponse } from '@paperdotnet/client';
import { fieldsOf } from '@paperdotnet/client';
import {
  createColumnHelper,
  rowSelectionFeature,
  tableFeatures,
  useTable,
  type RowSelectionState,
} from '@tanstack/react-table';
import { ArrowDown, ArrowUp, ChevronsUpDown, Folder } from 'lucide-react';
import { useMemo, type KeyboardEvent } from 'react';
import { Checkbox } from '@/components/ui/select';
import { FieldValue } from '@/features/fields/display';
import type { FieldDefinition } from '@/features/fields/values';
import { useFormat } from '@/lib/preferences';
import { cn } from '@/lib/utils';
import { fieldLabel } from './schema';

const features = tableFeatures({ rowSelectionFeature });
const helper = createColumnHelper<typeof features, ItemResponse>();

export interface Sort {
  field: string;
  descending: boolean;
}

/** OData order of a sort: fields/title desc. Built-in properties are not under fields/. */
export function orderByOf(sort: Sort | undefined): string | undefined {
  if (!sort) return undefined;
  const property = sort.field === 'updatedAt' || sort.field === 'createdAt' ? sort.field : `fields/${sort.field}`;
  return `${property}${sort.descending ? ' desc' : ''}`;
}

/** The items of a list as a table: server-side sort by column, selection, keyboard (arrows, Enter, x). */
export function ItemsTable({
  items,
  fields,
  sort,
  onSort,
  selection,
  onSelectionChange,
  onOpen,
  onOpenFolder,
  activeId,
}: {
  items: ItemResponse[];
  fields: FieldDefinition[];
  sort?: Sort;
  onSort: (sort: Sort | undefined) => void;
  selection: RowSelectionState;
  onSelectionChange: (selection: RowSelectionState) => void;
  onOpen: (item: ItemResponse) => void;
  onOpenFolder: (item: ItemResponse) => void;
  activeId?: string;
}) {
  const format = useFormat();
  const columns = useMemo(
    () =>
      helper.columns([
        helper.display({
          id: 'select',
          header: ({ table }) => (
            <Checkbox
              aria-label="Select all"
              checked={table.getIsAllRowsSelected()}
              ref={(el) => {
                if (el) el.indeterminate = table.getIsSomeRowsSelected();
              }}
              onChange={table.getToggleAllRowsSelectedHandler()}
            />
          ),
          cell: ({ row }) => (
            <Checkbox
              aria-label="Select"
              checked={row.getIsSelected()}
              onClick={(e) => e.stopPropagation()}
              onChange={row.getToggleSelectedHandler()}
            />
          ),
        }),
        ...fields.map((field) =>
          helper.display({
            id: field.name!,
            header: () => fieldLabel(field),
            cell: ({ row }) =>
              field.name === 'title' ? (
                <span className="flex items-center gap-2 font-medium">
                  {row.original.isFolder && <Folder className="size-4 shrink-0 fill-current/20 text-accent" />}
                  <span className="truncate">{String(fieldsOf(row.original).title ?? 'Untitled')}</span>
                </span>
              ) : (
                <FieldValue field={field} value={fieldsOf(row.original)[field.name!]} />
              ),
          }),
        ),
        helper.display({
          id: 'updatedAt',
          header: () => 'Modified',
          cell: ({ row }) => (
            <span className="whitespace-nowrap text-muted" title={format.dateTime(row.original.updatedAt)}>
              {format.relative(row.original.updatedAt)}
            </span>
          ),
        }),
      ]),
    [fields, format],
  );

  const table = useTable({
    features,
    columns,
    data: items,
    getRowId: (item) => item.id!,
    state: { rowSelection: selection },
    onRowSelectionChange: (updater) => onSelectionChange(typeof updater === 'function' ? updater(selection) : updater),
  });

  const open = (item: ItemResponse) => (item.isFolder ? onOpenFolder(item) : onOpen(item));
  const onKeyDown = (event: KeyboardEvent<HTMLTableRowElement>, item: ItemResponse) => {
    const row = event.currentTarget;
    if (event.key === 'Enter') open(item);
    else if (event.key === 'x') {
      const next = { ...selection };
      if (next[item.id!]) delete next[item.id!];
      else next[item.id!] = true;
      onSelectionChange(next);
    } else if (event.key === 'ArrowDown') (row.nextElementSibling as HTMLElement | null)?.focus();
    else if (event.key === 'ArrowUp') (row.previousElementSibling as HTMLElement | null)?.focus();
    else return;
    event.preventDefault();
  };

  return (
    <div className="overflow-x-auto rounded-lg border bg-surface">
      <table className="w-full border-collapse text-[13px]">
        <thead className="bg-surface-muted/60 text-left text-xs text-muted">
          {table.getHeaderGroups().map((group) => (
            <tr key={group.id}>
              {group.headers.map((header) => {
                const sortable = header.column.id !== 'select';
                const active = sort?.field === header.column.id;
                return (
                  <th
                    key={header.id}
                    className={cn(
                      'border-b px-3 py-2 font-medium whitespace-nowrap',
                      header.column.id === 'select' && 'w-10',
                    )}
                    aria-sort={active ? (sort!.descending ? 'descending' : 'ascending') : undefined}
                  >
                    {sortable ? (
                      <button
                        type="button"
                        className="inline-flex items-center gap-1 hover:text-foreground"
                        onClick={() =>
                          onSort(
                            !active
                              ? { field: header.column.id, descending: false }
                              : !sort!.descending
                                ? { field: header.column.id, descending: true }
                                : undefined,
                          )
                        }
                      >
                        <table.FlexRender header={header} />
                        {active ? (
                          sort!.descending ? (
                            <ArrowDown className="size-3" />
                          ) : (
                            <ArrowUp className="size-3" />
                          )
                        ) : (
                          <ChevronsUpDown className="size-3 opacity-40" />
                        )}
                      </button>
                    ) : (
                      <table.FlexRender header={header} />
                    )}
                  </th>
                );
              })}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map((row) => (
            <tr
              key={row.id}
              tabIndex={0}
              aria-selected={row.getIsSelected()}
              onClick={() => open(row.original)}
              onKeyDown={(e) => onKeyDown(e, row.original)}
              className={cn(
                'cursor-pointer border-b outline-none last:border-b-0 hover:bg-surface-muted/60 focus-visible:bg-accent-soft/40',
                row.getIsSelected() && 'bg-accent-soft/40',
                activeId === row.id && 'bg-accent-soft/60',
              )}
            >
              {row.getAllCells().map((cell) => (
                <td key={cell.id} className="max-w-80 px-3 py-2 align-middle">
                  <table.FlexRender cell={cell} />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
