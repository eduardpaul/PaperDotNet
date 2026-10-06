import { fieldsOf } from '@paperdotnet/client';
import { useQuery } from '@tanstack/react-query';
import { ArrowDown, ArrowUp } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { listBuilder } from '@/features/lists/queries';
import { filePath } from '@/features/documents/paths';
import { useAuthedImage } from '@/lib/authed-image';
import type { SelectionPresentation } from './launch-model';

function SelectionImage({ workspaceId, listId, id }: { workspaceId: string; listId: string; id: string }) {
  const image = useAuthedImage(filePath(workspaceId, listId, id));
  return image.url && !image.failed ? (
    <img src={image.url} alt="Selected image" className="h-14 w-12 object-contain" />
  ) : null;
}

export function SelectionItems({
  workspaceId,
  listId,
  itemIds,
  primaryItemId,
  onPrimary,
  onOrder,
  disabled,
  presentation,
}: {
  workspaceId: string;
  listId: string;
  itemIds: string[];
  primaryItemId?: string;
  onPrimary: (id: string) => void;
  onOrder: (ids: string[]) => void;
  disabled: boolean;
  presentation?: SelectionPresentation;
}) {
  const { data = [] } = useQuery({
    queryKey: ['workflow-selection', workspaceId, listId, ...[...itemIds].sort()],
    queryFn: () => Promise.all(itemIds.map((id) => listBuilder(workspaceId, listId).items.byItemId(id).get())),
  });
  const names = new Map(data.map((item) => [item?.id, String(fieldsOf(item ?? {}).title ?? item?.id ?? '')]));
  const move = (from: number, to: number) => {
    const next = [...itemIds];
    [next[from], next[to]] = [next[to]!, next[from]!];
    onOrder(next);
  };
  return (
    <div className="space-y-3">
      <Label htmlFor="primary-item">Primary item</Label>
      <Select
        id="primary-item"
        value={primaryItemId}
        disabled={disabled}
        onChange={(event) => onPrimary(event.target.value)}
      >
        {itemIds.map((id) => (
          <option key={id} value={id}>
            {names.get(id) ?? id}
          </option>
        ))}
      </Select>
      {presentation?.primaryDescription && <p className="text-sm text-muted">{presentation.primaryDescription}</p>}
      <ol aria-label={presentation?.orderLabel ?? 'Selection order'} className="space-y-2">
        {itemIds.map((id, index) => (
          <li key={id} className="flex items-center gap-2 rounded border p-2">
            {presentation?.preview === 'image' && <SelectionImage workspaceId={workspaceId} listId={listId} id={id} />}
            <span className="min-w-0 flex-1 truncate">
              {index + 1}. {names.get(id) ?? id}
            </span>
            <Button
              type="button"
              size="sm"
              aria-label={`Move ${presentation?.itemLabel ?? 'item'} ${index + 1} up`}
              disabled={disabled || index === 0}
              onClick={() => move(index, index - 1)}
            >
              <ArrowUp />
            </Button>
            <Button
              type="button"
              size="sm"
              aria-label={`Move ${presentation?.itemLabel ?? 'item'} ${index + 1} down`}
              disabled={disabled || index === itemIds.length - 1}
              onClick={() => move(index, index + 1)}
            >
              <ArrowDown />
            </Button>
          </li>
        ))}
      </ol>
    </div>
  );
}
