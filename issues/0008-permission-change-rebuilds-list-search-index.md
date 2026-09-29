# 0008: A permission change rebuilds the list's whole search index and drops its embeddings

- **Status:** done
- **Area:** Search
- **Date:** 2026-09-28

## Problem

Every permission change on a list or on any item in it (break or reset
inheritance, replace grants), and every folder move or restore that changes
scopes, publishes `ListIndexInvalidated` for the whole list. The subscriber
deletes all search documents of the list and indexes every item again. Item
text contributors run again for each item (for documents: all page texts).
The delete also removes every passage, so passages come back without
embeddings and the embedding job embeds the whole list again. Sharing one
folder in a library of 100,000 documents re-reads and re-embeds all 100,000.

The index rows that actually change are the reader principals, and only for
the items under the changed scope.

## Where it shows up

- [`PermissionEndpoints`](../src/Modules/Lists/PaperDotNet.Lists/Features/PermissionEndpoints.cs)
  and [`ItemWriter`](../src/Modules/Lists/PaperDotNet.Lists/Features/ItemWriter.cs)
  (folder move, restore) publish `ListIndexInvalidated`.
- [`ListItemSearchDocuments.IndexListAsync`](../src/Modules/Lists/PaperDotNet.Lists/Features/ItemSearchIndexer.cs)
  calls `DeleteContainerAsync`, then rebuilds every item.
- [`SearchIndex.DeleteContainerAsync`](../src/Modules/Search/PaperDotNet.Search/Features/SearchIndex.cs)
  deletes passages (and their embeddings) with the documents.

## Possible approaches

- Store the item's scope id on the search document and trim by the caller's
  allowed scopes at query time. A grant change then writes nothing to the
  index; a scope rewrite updates one column of the affected documents
  ([item-and-permission-storage.md](../docs/item-and-permission-storage.md),
  decision 3).
- Smaller step: rewrite `document_principals` for the affected items only,
  without deleting documents or passages.

## Seen (2026-09-29)

Seeding the `shared` performance scenario on PostgreSQL (500 folders, about 750 grant changes on one list) started as
many full reindexes of that list. They raced: 2,526 `duplicate key … pk_document_principals` errors and 18 deadlocks
in the server log, retried by the messaging layer. The API answered without errors.

## Done (2026-09-29)

[ADR-0035](../docs/adr/0035-item-storage-and-permissions-at-scale.md) step 3. Search documents store the item's scope and search trims with the caller's readable scopes
(`IItemAccess.GetScopesAsync`); `document_principals` is gone. A grant change writes nothing to the index. Items that
move to another scope get their scope column updated (`ItemScopesChanged`, `ISearchIndex.SetScopesAsync`); passages and
embeddings stay. Existing indexes need one reindex after the upgrade (documents start with an empty scope).
