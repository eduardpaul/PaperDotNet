import { keys } from '@/api/keys';
import type { ItemPanelContext } from '@/extensibility/item-panels';
import { listBuilder } from '@/features/lists/queries';
import { PermissionsEditor } from './permissions-editor';

/** Who may see and change this item (IAM-07): inherited from the list, or its own. */
export function AccessTab({ workspaceId, list, item }: ItemPanelContext) {
  return (
    <div className="p-5">
      <PermissionsEditor
        queryKey={[...keys.item(workspaceId, list.id!, item.id!), 'permissions']}
        builder={listBuilder(workspaceId, list.id!).items.byItemId(item.id!).permissions}
        scope={item.isFolder ? 'folder' : 'item'}
      />
    </div>
  );
}
