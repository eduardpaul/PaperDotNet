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

## Typed relationships and knowledge graphs

A relationship can have a type such as **contains receipt line**, **references** or **generated from**. Select a type
in Related or create one in the open vocabulary. Types are terms in **Relationships / Types**, so their ids survive
renaming and their synonyms resolve to the same predicate. Manage names, synonyms and translations in the term store.

Types can be symmetric or directed. A directed type can have an inverse label: the receipt shows **contains receipt
line**, while the line shows **belongs to receipt**. Both endpoints display the same edge. Choose the source when
linking, and filter Related by type or by incoming/outgoing direction. Different types can connect the same two
items; unlinking removes only the selected edge.

A directed type can limit incoming or outgoing links per item. For example, maximum incoming 1 prevents a line from
belonging to two receipts. Limits count preserved links to recycled items too. They do not require every item to have
a parent: use a workflow to create and link a line. Direction, inverse label and limits are fixed at type creation;
use a new predicate for different behavior. Constrained types with edges cannot be merged; unconstrained compatible
merges deduplicate matching edges. Deprecating a term stops new assignments and leaves existing relationships intact.

| Request | Behavior |
|---|---|
| `GET /v1.0/relationshipTypes` | Assignable terms with direction, inverse label and limits. |
| `POST /v1.0/relationshipTypes` | Ensure a type: `{ "name": "contains receipt line", "directed": true, "inverseLabel": "belongs to receipt", "maxIncoming": 1 }`. |
| `GET /v1.0/items/{id}/relationships?type=contains%20receipt%20line&direction=outgoing` | Paged edges; type accepts a term id or label, direction is both/incoming/outgoing. |
| `POST /v1.0/items/{id}/relationships` | Ensure an edge with `{ "otherId": "…", "type": "contains receipt line" }`. Direction defaults to the type; an unknown label creates a type. |
| `DELETE /v1.0/items/{id}/relationships/{relationshipId}` | Remove one edge from either endpoint. |

An edge returns `id`, `sourceItemId`, `targetItemId`, `directed`, `type`, and `relatedItem` (the readable peer with its
current location). An omitted type is untyped; without a type you can set `directed: true` explicitly. The legacy
`relations` GET lists distinct peers across all edge types; its PUT/DELETE operate only on the untyped symmetric link.
Relationship types require list scopes like the graph API; managing taxonomy labels uses the existing taxonomy scopes.

MCP adds `list_item_relationships`, `list_relationship_types`, `create_relationship_type` and `remove_item_relationship`.
`relate_items` accepts optional `type` and `directed`; `related=false` removes the legacy untyped symmetric link.

The [receipts package](../samples/receipts-package/README.md) demonstrates paged `items.related` reads and planned
`items.relate`, `items.unrelate` and `items.deleteById` writes. Its lines carry purchase details; global relationships
carry receipt membership. See [ADR-0040](adr/0040-typed-item-relationships.md).


## Relationship attributes and workspace queries

Edges accept an optional flat `attributes` object. Values can be strings, finite numbers or booleans; keys must be ASCII
identifiers (use `receiptReader_confidence` for extension prefixes). Limits: 64 keys, 128 characters per key, 16 KiB total.
They describe the connection, and are shared by both endpoints. Item fields still describe each item.

```http
POST /v1.0/items/{sourceId}/relationships
Content-Type: application/json

{"otherId":"{targetId}","type":"references","attributes":{"confidence":0.6,"origin":"reader"}}
```

Repeated POST leaves an existing edge unchanged. Read `/v1.0/items/{eitherEndpointId}/relationships/{edgeId}` for its ETag,
then PATCH the same URL with `If-Match: "{version}"` and `{"attributes":{"confidence":0.9,"origin":null}}`.
Null removes a key. Missing/malformed If-Match returns 428; a stale version returns 412. Update requires Contribute on both
endpoints. All graph responses include `attributes` and `version`.

```http
GET /v1.0/workspaces/{workspaceId}/relationships?$filter=attributes/confidence le 0.7
GET /v1.0/workspaces/{workspaceId}/relationships?type=references&directed=true&$top=50
```

Each workspace result contains `id`, `type`, `directed`, `attributes`, `version`, `sourceItem` and `targetItem`. Both item
objects use the global located-item shape. Either endpoint must currently belong to the workspace, and both must be readable.
Symmetric edges appear once; source/target identify canonical storage order, without implying direction. Cross-workspace edges
appear in either workspace's query. Moving items changes workspace query membership without changing the edge or its attributes.
Recycled endpoints exclude their edges from queries until restored.

Filters support eq/ne, numeric lt/le/gt/ge, and/or/not, and `id`/`directed`. Strings/booleans support eq/ne. Non-null comparisons,
including ne, do not match missing keys or a different JSON value type. `attributes/key eq null` matches missing keys. Nested
paths, functions and property-to-property comparisons return 400. Filters are limited to 4096 characters. Filters and permissions
are applied in the database before paging; follow `@odata.nextLink`, which preserves query options. Edge-id ordering is fixed.

Extensions use `IListItemStore.QueryRelationshipsAsync`, `GetRelationshipAsync` and `UpdateRelationshipAsync`. Workflows use
`items.relationships({filter: 'attributes/confidence le 0.7'})`, `items.relate(sourceId, targetId, type, attributes)`, and
`items.updateRelationship(sourceId, edgeId, attributes, version)`. MCP exposes `query_workspace_relationships` and
`update_relationship_attributes`, and `relate_items` accepts attributes. The UI continues to show links and predicates.

Export/import preserves attribute bags. Merging taxonomy predicates merges disjoint/equal keys on duplicate edges and rejects
conflicting values; it never silently chooses one bag. See [ADR-0041](adr/0041-relationship-attributes-and-workspace-queries.md).
