# 0003: Loading a user's permission scopes preloads every unique scope in the list

- **Status:** done
- **Area:** Lists
- **Date:** 2026-09-27

## Problem

[ADR-0011](../docs/adr/0011-permission-scopes.md) assumes a list has "few
rows" of unique permission scopes, so `ListSchemaLoader` can afford to load
all of them per request. As soon as a list has many folders or items with
broken inheritance (a common pattern for sharing individual folders with
specific people, e.g. per-client folders in one library), `GetAccessAsync`
loads the full set of unique scope ids for the *whole list*, and
`GrantsForUserAsync` loads every grant on the list, regardless of which items
the current request actually needs. The cost grows with the size of the list,
not with the size of the request.

## Where it shows up

- [`ListSchemaLoader.GetAccessAsync`](../src/Modules/Lists/PaperDotNet.Lists/Features/ListSchema.cs):
  `db.Items.Where(i => i.ListId == list.Id && i.HasUniquePermissions).Select(i => i.Id).ToListAsync(ct)`
  and the grants query right above it, both scoped to the whole list.
- Every endpoint that calls `ListSchemaLoader.LoadAsync` inherits this cost
  (item read/write, query, delta sync, permission endpoints).

## Possible approaches

- Compute only the scopes relevant to the current request (e.g. join grants
  against the candidate item set of the query instead of materializing every
  unique scope in the list) rather than a full-list preload.
- Cap or page scope enumeration, and fall back to a per-item check when the
  count is large.
- Combine with the caching in [0002](0002-uncached-permission-lookups-on-every-request.md)
  so the cost is paid once per change instead of once per request.

**This needs more investigation before scheduling:** it is not yet known how
many unique-permission scopes are realistic in a single list for real usage,
or whether tenants actually hit this pattern often enough to matter. A
reproduction with a list containing hundreds or thousands of broken-inheritance
folders, measured with `PaperDotNet.Performance`, would confirm the cost
before choosing a fix.

## Measured (2026-09-28)

With [the storage benchmark](../tests/benchmarks/item-storage/README.md) (a library laid out like a Papermerge
import: 1M documents, 7,500 unique scopes), loading every unique scope id took
84–94 ms on PostgreSQL and 260–265 ms on SQLite per request. No index serves
`HasUniquePermissions`, so PostgreSQL scans all of `items` in parallel. A
partial index brings it to 5.1 ms, but it still grows with the number of
unique scopes. Options: [item-and-permission-storage.md](../docs/item-and-permission-storage.md), decision 1.

## Done (2026-09-29)

[ADR-0035](../docs/adr/0035-item-storage-and-permissions-at-scale.md) step 1. Every item has a scope (the list id when
it inherits) and the caller's allowed scopes come from `acl_entries` by principal, so the list's other scopes are never
loaded. The filter is one parameter on both databases (`EF.Parameter`). `PaperDotNet.Performance` measures it with the
`shared` scenario (see [performance.md](../docs/performance.md)).
