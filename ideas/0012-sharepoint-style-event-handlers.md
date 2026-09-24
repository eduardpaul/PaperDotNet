# 0012: Event handlers like SharePoint event receivers (before / after)

- **Status:** new
- **Area:** Extensions / Platform
- **Date:** 2026-09-24
- **Mapped to:** refines extension point 5 in [architecture-vision.md](../docs/architecture-vision.md) section 3.2

## The idea

Extension event handlers work like SharePoint event receivers, with two kinds
of events:

- **Before events** ("-ing": `ItemAdding`, `ItemUpdating`, `ItemDeleting`, …)
  - run **synchronously**, before the change is saved
  - can **inspect and change** the incoming values
  - can **cancel** the operation with an error message returned to the caller
- **After events** ("-ed": `ItemAdded`, `ItemUpdated`, `ItemDeleted`, …)
  - run after the change is committed
  - either **synchronous** (inside the request, after commit) or
    **asynchronous** (background, via the event outbox)
  - cannot cancel the change

This applies to extensions in every language: .NET in-process, and Node.js /
Python (idea 0011).

## Why / problem it solves

- Validation and enforcement that can't be expressed as field rules. Examples:
  - "an invoice needs a PO number when amount > 1000"
  - "a closed task can't be edited"
  - "only tags from term set X on this list"
- Enrichment before save: default values, computed fields, auto-tags
  (ideas 0008, 0009).
- Side effects after save: notifications, sync, indexing, webhooks.
- The SharePoint model is familiar and proven.

## Examples / references

- SharePoint event receivers (`SPItemEventReceiver`):
  - `BeforeProperties` / `AfterProperties`
  - `properties.Status = CancelWithError` plus `ErrorMessage`
  - `Synchronization` (sync or async) for after events
  - `SequenceNumber` for ordering
  - `EventFiringEnabled` to prevent recursion
  - registration per list, list template, content type or site
  - remote event receivers for out-of-process code
- Architecture vision section 3.2, extension point 5 ("before handlers (sync;
  can validate, modify or cancel) and after handlers (async)"). This idea
  details that point and adds sync after events.

## Notes

<!-- Proposed design, to confirm during review:
     - Event families:
         - Item: Adding/Added, Updating/Updated, Deleting/Deleted,
           Moving/Moved, Restoring/Restored
         - File (libraries): FileAdding/Added, VersionAdding/Added, etc.
         - Tag/metadata: TagsChanging/Changed (idea 0008)
         - List/schema: ListAdding/Added, FieldAdding/Added, ContentTypeAdding/Added
         - Workspace, sharing/permissions, tenant (lifecycle) events
     - Registration (in the manifest or at runtime), scoped to one of:
       tenant, workspace, list, list template, or content type.
       Also: filter conditions, and a sequence number for ordering.
     - Handler context:
         - tenant, user and workspace (idea 0005)
         - Before values (the current stored item)
         - After values (the incoming change, editable in before events)
         - changed-field list
         - Cancel(message, code), mapped to an API error such as 409 or 422
         - correlation id
     - Transactions:
         - before handlers run inside the unit of work, before
           SaveChanges, so their edits are saved atomically
         - sync after handlers run after commit and cannot roll back
         - async after handlers go through the outbox, with retries and a
           dead-letter queue
     - Recursion: changes made by a handler fire events again, unless the
       handler uses a "suppress events" scope, like SharePoint's
       EventFiringEnabled. Depth limit as a safety net.
     - Bulk operations: per-item events or batch events
       (ItemsUpdating with a collection)?
     - Timeouts and failure policy per handler: fail-closed (block the
       change) or fail-open (log and continue). Most important for
       Node/Python sidecar handlers (idea 0011), where before events go
       over RPC with a short timeout.
     - Relation to automation (idea 0009): Elsa workflows are started by
       after events. Before events stay code-only. Should simple before
       rules be allowed in the rules engine (validation rules)?
     - Relation to webhooks and MCP (ideas 0003, 0004): external
       subscribers only get async after events, like SharePoint/Graph
       webhooks. -->
