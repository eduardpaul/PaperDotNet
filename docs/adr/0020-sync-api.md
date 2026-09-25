# ADR-0020: Comments, activity and the sync API

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Phase 5d brings:

- comments and an activity timeline on items (LST-17);
- JSON batching (API-04);
- delta sync (API-05);
- change notifications (API-06).

Offline clients and integrations need to find changes without polling whole
lists. Every approach must work on SQLite and PostgreSQL alike.

## Decision

- **Change log in the lists engine.**
  - `ListsDbContext` writes an `ItemChange` row (sequence, list, item, scope,
    kind) for every tracked item change, in the same transaction.
  - The rows come from the change tracker, so every write path is covered:
    API, `IListItemStore`, bulk updates, restores and purges.
  - Permission changes (grants, inheritance, a new security scope of an item)
    write a `Reset` row for the list.
  - Delta tokens hold a sequence and a time.
    - A `Reset` after the token means 410 `resyncRequired`.
    - So does a token older than the retention (default 30 days; a daily job
      prunes the log).
  - Delta only reads changes older than a small safety window, because
    identity values on PostgreSQL can commit out of order.
  - The response is filtered with the caller's current access. Deleted items
    are only reported to callers who could read them (by the scope stored in
    the log). So ids of hidden items do not leak.
- **Collaboration module on the SDK** (like Documents and Tasks).
  - It holds comments and timeline entries in its own schema.
  - Access comes from `IListItemStore`. Mentions notify through
    `INotificationSender` after a read check as the mentioned user.
  - Comment text is added to search with `IItemSearchContributor`.
  - The timeline is filled from item events (idempotent by event id).
  - `IItemActivity` (Collaboration.Contracts, part of the SDK) lets modules and
    extensions add entries; Automation records approval decisions.
- **Change subscriptions in the Notifications module.** They reuse its signing,
  SSRF-guarded HTTP client and backoff. Graph-like shape:
  - resource, change types, client state and expiry (max 30 days, renewable);
  - a validation handshake before creation;
  - payloads with ids only.

  Deliveries are queued by an item-event subscriber after a read check as the
  owner, one per subscription and event, and posted by a tenant job every
  10 seconds.
- **`$batch` in the host.** It is a branched pipeline: routing to the
  application's endpoints, tenant resolution, authentication, the tenant guard
  and authorization.
  - Each sub-request gets its own DI scope and runs sequentially.
  - It inherits only the authentication and tenant headers.
  - Nested batches and absolute URLs are refused.
  - No transaction across requests.

## Consequences

- The change log adds one row per item change. That is cheap, and it is pruned
  after the retention.
- Permission changes force delta clients into a full sync, a rare event.
  Workspace membership changes are not in the log: losing access shows as 404,
  and gaining access needs a new sync.
- Deletion notifications check read access on the list, because the item is
  gone. A subscriber may learn the id of a deleted item it could not read, but
  never any data.
- Delta, subscriptions and batching cover lists, including document libraries,
  tasks and calendars. Other resources (users, taxonomy) can add their own
  change logs later.
