import { useQuery } from '@tanstack/react-query';
import { ItemPanel } from './item-panel';
import { listQuery } from './queries';

/** The item panel for an item of any list (smart folders, search): loads the list first. */
export function ItemPanelLoader({
  workspaceId,
  listId,
  itemId,
  tab,
  onTab,
  onClose,
  onCreated,
  initialValues,
}: {
  workspaceId: string;
  listId: string;
  /** An item id, or "new". */
  itemId: string;
  tab: string;
  onTab: (tab: string) => void;
  onClose: () => void;
  onCreated?: (itemId: string) => void;
  initialValues?: Record<string, unknown>;
}) {
  const { data: list } = useQuery(listQuery(workspaceId, listId));
  if (!list) return null;
  return (
    <ItemPanel
      workspaceId={workspaceId}
      list={list}
      itemId={itemId}
      tab={tab}
      onTab={onTab}
      onClose={onClose}
      onCreated={onCreated ?? (() => {})}
      initialValues={initialValues}
    />
  );
}
