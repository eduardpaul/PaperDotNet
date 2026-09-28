# 0004: Breaking or resetting inheritance on a large folder is one large inline transaction

- **Status:** confirmed
- **Area:** Lists
- **Date:** 2026-09-27

## Problem

Breaking or resetting permission inheritance on a folder rewrites the
`ScopeId` of every descendant in the subtree, batched with `ExecuteUpdate`
inside the same request/transaction as the API call. This is correct and
avoids one round trip per item, but for a folder with a large number of
descendants it becomes a long-running transaction that holds row locks on
`items` while other writers on the same list wait. On SQLite specifically
this contends with the single-writer model.

Documents (or other search-indexed content) under the changed folder also
need their `document_principals` rows rewritten, which is a second,
separate write under the same permission change, compounding the cost.

## Where it shows up

- [`ScopeTree.ReassignAsync`](../src/Modules/Lists/PaperDotNet.Lists/Features/PermissionEndpoints.cs)
  (batched `ExecuteUpdate` per level, called synchronously from
  `PermissionEndpoints`).
- [`ItemSearchIndexer`](../src/Modules/Lists/PaperDotNet.Lists/Features/ItemSearchIndexer.cs),
  which re-derives `document_principals` from item scopes.
- [performance.md](../docs/performance.md) notes concurrency was never raised
  above one caller and PostgreSQL was not measured, so this path has no
  baseline at all.

## Possible approaches

- Route large reassignments through `IOperations.StartAsync` (the existing
  202 + background-operation mechanism) instead of doing them inline in the
  request, once the affected count crosses a threshold.
- Chunk the `ExecuteUpdate` batches with smaller transactions and yield
  between them, instead of one transaction for the whole subtree.
- Decouple the search reindex from the permission-change transaction (it
  already goes through the outbox/event pipeline for other changes; confirm
  it does here too and isn't done inline).

**This needs more investigation before scheduling:** there is no measurement
of how large a subtree needs to be before this becomes noticeable, nor
confirmation of whether the search reindex for a scope change is already
async via the outbox or run inline. Both should be checked before deciding
on an approach.

## Measured (2026-09-28)

With [the storage benchmark](../tests/benchmarks/item-storage/README.md), rewriting the scope of 100,000 items in
one statement took 5.1 s on PostgreSQL, holding row locks on the subtree. On
SQLite it took 2.06 s under the database-wide write lock: an edit in another
list waited 2.04 s. At about 250,000 rows the 5 s `busy_timeout` would expire
and other writes would fail. 10,000 rows took 871 ms and 109 ms. The search
side is worse than assumed: the whole list is reindexed
([0008](0008-permission-change-rebuilds-list-search-index.md)). Options:
[item-and-permission-storage.md](../docs/item-and-permission-storage.md), decision 3.
