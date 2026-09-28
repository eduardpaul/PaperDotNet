# 0002: Permission and membership lookups are uncached on every list/item request

- **Status:** confirmed
- **Area:** Lists
- **Date:** 2026-09-27

## Problem

Every request that touches a list (read, write, or query an item) resolves the
caller's effective permissions from scratch, with no caching layer, even
though the same lookups are almost always unchanged from one request to the
next for the same user.

`IWorkspaceAccess.GetPermissionAsync` queries `Members` directly.
`IUserDirectory.GetGroupIdsAsync` queries group membership directly.
`ListSchemaLoader.GetAccessAsync` then, when a list or any of its items has
unique permissions, loads **every unique-permission item id in the list**
plus the user's grants (joined against the uncached group ids) on every call.
None of these three lookups go through `HybridCache`, unlike the RBAC scope
lookup (`EffectiveScopeProvider`), which already caches per user/tenant with
tag-based invalidation.

## Where it shows up

- [`WorkspaceAccess.GetPermissionAsync`](../src/Modules/Workspaces/PaperDotNet.Workspaces/Features/WorkspaceAccess.cs)
- [`UserDirectory.GetGroupIdsAsync`](../src/Modules/Identity/PaperDotNet.Identity/Features/UserDirectory.cs)
- [`ListSchemaLoader.GetAccessAsync` / `GrantsForUserAsync`](../src/Modules/Lists/PaperDotNet.Lists/Features/ListSchema.cs)
- Contrast with the cached pattern already in use:
  [`EffectiveScopeProvider`](../src/Modules/Identity/PaperDotNet.Identity/Features/EffectiveScopeProvider.cs)

## Possible approaches

- Cache `GetPermissionAsync` and `GetGroupIdsAsync` in `HybridCache` with a
  short TTL and tag-based invalidation on membership/role/group changes, the
  same shape as `EffectiveScopeProvider`.
- Cache the per-list `(listLevel, scopes)` result of `GetAccessAsync` keyed by
  `(tenant, list, user)`, invalidated by the existing `ItemChange` "Reset"
  signal that permission changes already emit for delta sync.

**This needs more investigation before scheduling:** there is no measurement
of how many extra round trips this adds under concurrent load, nor of how
often the caches would actually hit versus be invalidated (permission changes
may be frequent enough in some tenants to limit the benefit). A concurrency
ramp in `PaperDotNet.Performance` against a list with several unique
permission scopes would confirm whether this is worth doing before other,
cheaper fixes (see [0001](0001-list-pages-slow-as-a-folder-grows.md)).

## Measured (2026-09-28)

With [the storage benchmark](../tests/benchmarks/item-storage/README.md) (a library of 1M documents with 7,500
unique scopes), the four access queries took 122 ms on PostgreSQL and 321 ms
on SQLite per request. Nearly all of that is the unique-scope load of
[0003](0003-permission-scope-preload-grows-with-list-size.md). A list page
request (access and page) reached 9, 13 and 12 requests/s with 1, 4 and 16
clients on 4 cores. Looking the allowed scopes up by principal took 0.16 ms
and 0.07 ms, and the same request reached 5,289 requests/s with 4 clients.
Options: [item-and-permission-storage.md](../docs/item-and-permission-storage.md), decision 1.
