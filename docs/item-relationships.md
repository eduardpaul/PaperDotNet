# Related items and permanent links

Every document, task, event, note and custom content item has a stable id. Open **Related** in its item panel to link
multiple items across lists and workspaces. A link appears on both items immediately. You must be able to edit both
items to add or remove it. Only items you can read appear in the related list.

The **Permanent link to this item** uses `/i/{id}` and follows the item's current location. Links survive renaming,
file replacement and moves. Recycling hides the item but keeps its relationships for restore; permanent deletion
removes them. Copying creates a separate item without copying its relationships.

Use **File in a library…** for documents or **Move to a list…** for other items. The destination must support the
same content type and list kind. The move preserves identity, field values, versions, comments and relationships.
The item inherits access from the destination list or folder. Folder trees currently move only within their list.

## API

| Request | Behavior |
|---|---|
| `GET /v1.0/items?q=invoice&workspaceId=…&writable=true` | Paged title search over accessible content items; parameters are optional. |
| `GET /v1.0/items/{id}` | Item with its current workspace/list, names and `canRelate`; item ETag is also returned. |
| `GET /v1.0/items/{id}/relations` | Paged accessible related items with current locations. |
| `PUT /v1.0/items/{id}/relations/{otherId}` | Add one symmetric link; repeated and inverse requests are idempotent. |
| `DELETE /v1.0/items/{id}/relations/{otherId}` | Remove the shared link from both items; repeated requests are idempotent. |
| `POST /v1.0/items/{id}/move` | Move with `{ "workspaceId": …, "listId": …, "parentId": … }` and the item's `If-Match`. |

Reads require `list.read`; writes require `list.write`, in addition to item permissions. All identities resolve within
the current tenant. Related lists and searches support `$top` and `$skiptoken`; follow `@odata.nextLink` for more.
Locations are returned as `workspaceId`, `workspaceName`, `listName` and `contentTypeName`, with the normal item in
`item`. Use `item.id` as the stable identity and `item.listId` as the current container.

Packages retain relationships between included items and remap their ids on import. Workspace exports omit
relationships to other workspaces with a warning. Missing import targets are skipped with a warning.

Extensions use `IListItemStore.GetByIdAsync`, `GetRelatedAsync`, `RelateAsync` and `MoveToAsync`. A module that keeps
item locations in its own tables implements `IItemMoveParticipant` to update them inside the move transaction.
MCP exposes `get_global_item`, `list_related_items`, `relate_items` and `move_item_to_list` with the caller's permissions.

Design: [ADR-0039](adr/0039-global-item-relationships.md).
