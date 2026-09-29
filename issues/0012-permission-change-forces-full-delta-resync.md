# 0012: Any permission change makes every delta client of the list sync it again

- **Status:** done
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

## Done (2026-09-29)

[ADR-0035](../docs/adr/0035-item-storage-and-permissions-at-scale.md) step 3. Items that move to another scope are logged one by one with the scope they came from; a changed access list
logs a `ScopeChanged` marker. Delta returns affected items as changed, or as removed (`"reason": "changed"`) when the
caller could read them before and cannot now. A marker for a scope with more than `Lists:DeltaScopeLimit` items (1,000)
answers 410. For a marker delta cannot tell whether the caller had the items, so it may report ids the caller never
saw as removed.
