# 0012: Any permission change makes every delta client of the list sync it again

- **Status:** possible
- **Area:** API
- **Date:** 2026-09-28

## Problem

A grant change, a break or reset of inheritance, or a folder move across
scopes writes a `Reset` into the list's change log. The next delta call of
every client of that list returns 410 `resyncRequired`, so each one downloads
the whole list again. For a sync client of a large library, sharing one
folder with one person costs a full download for everyone.

## Where it shows up

- `ListsDbContext.RecordChanges` in
  [`ListsDbContext`](../src/Modules/Lists/PaperDotNet.Lists/Data/ListsDbContext.cs)
  (Reset on `ScopeId`, `HasUniquePermissions` or grant changes)
- [`DeltaEndpoints`](../src/Modules/Lists/PaperDotNet.Lists/Features/DeltaEndpoints.cs)
  (410 when a Reset follows the token)

## Possible approaches

- Log the scope that changed instead of a list-wide reset. A delta call then
  returns the items of that scope as changed (the caller can now read them)
  or removed (they no longer can), and falls back to 410 only above a size
  limit ([item-and-permission-storage.md](../docs/item-and-permission-storage.md),
  decision 3).
