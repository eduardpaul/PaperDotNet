# 0005: No promotion path from a hot dynamic field to a real indexed column

- **Status:** done
- **Area:** Lists
- **Date:** 2026-09-27

## Problem

Dynamic item fields live in one JSON document column, translated through
`IJsonQueryFunctions` per provider. This works well for equality filters via
the containment index, but range filters, sorts, and aggregates on a JSON
field (a due date, a numeric status, a priority) cannot use an index on
either provider. [Issue 0001](0001-list-pages-slow-as-a-folder-grows.md)
already names this for sorting; the underlying gap is broader: there is no
general mechanism to promote a specific field of a specific list to a real
(or computed/expression) column with its own index when a tenant's usage
shows it is hot, so every list with a frequently sorted or range-filtered
dynamic field pays a full scan past a small number of rows.

## Where it shows up

- [`ListsDbContext`](../src/Modules/Lists/PaperDotNet.Lists/Data/ListsDbContext.cs):
  only `Title`, `CreatedAt`, `UpdatedAt`, `ParentId`, `ScopeId` are real,
  indexed columns; everything else is inside the `Fields` JSON document with
  one containment index.
- `docs/technical-approach.md` section 6 acknowledges this ("a due date or a
  status needs a real column or an expression index, one per database") but
  no such mechanism exists yet.

## Possible approaches

- A per-list, per-field opt-in that adds a generated/computed column (and a
  matching index) projected from the JSON document, implemented once per
  provider (`Persistence.Sqlite` / `Persistence.PostgreSql`).
- Alternatively, materialize commonly-sorted fields into dedicated columns
  automatically for built-in content types (e.g. task due date) rather than a
  general per-tenant mechanism.

**This needs more investigation before scheduling:** it's not clear yet
whether this should be a general per-tenant customization point (bigger,
reusable, but adds schema-migration-at-runtime complexity for a single-binary
self-hosted product) or a small, fixed set of promoted fields for built-in
content types only. That decision needs a look at which fields are actually
sorted/filtered by range in practice (tasks' due date, calendar dates) before
committing to either shape.

## Measured (2026-09-28)

With [the storage benchmark](../tests/benchmarks/item-storage/README.md) on 500,000 CRM deals:

| Query | JSON (PostgreSQL / SQLite) | Typed columns |
|---|---|---|
| One stage, sorted by close date | 160 ms / 475 ms | 1.9 ms / 1.6 ms |
| Amount range and date range, count | 101 ms | 4.6 ms |
| Count per stage (board) | 299 ms / 770 ms | 55 ms / 44 ms |

A typed pivot table was also measured and rejected: it joins badly when one
field filters and another sorts (148 ms), and costs the most on writes.
Options: [item-and-permission-storage.md](../docs/item-and-permission-storage.md), decision 2.

## Done (2026-09-29)

[ADR-0035](../docs/adr/0035-item-storage-and-permissions-at-scale.md) step 4. A field marked `indexed` is copied to one of 10 text, 10 number or 10 date columns of the item (per list,
`Lists:IndexedFields`), each with a partial index, or, with multiple values or as a reference, to `lists.item_values`.
Filters, sorting, grouping and `GET …/items/counts` use them; fields indexed later are backfilled in the background and
read from the JSON until then.
