# 0010: On SQLite, every equality filter on a field parses the JSON of every row

- **Status:** done
- **Area:** Lists
- **Date:** 2026-09-28

## Problem

Field equality filters (`fields/status eq 'Open'`, `assignedTo/any(…)`,
lookups, terms) are translated to `JsonFunctions.Contains`. On PostgreSQL
that is `@>` with a GIN index. SQLite has no such index, so the provider drops
it and calls the C# function `pdn_json_contains`, which parses the item's JSON
for every row of the list. Sorting and ranges on fields use `json_extract` and
also scan the list.

Measured with [the storage benchmark](../tests/benchmarks/item-storage/README.md)
on a 500,000-row list: `stage = 'Negotiation'` sorted by `closeDate` took
475 ms with `json_extract` and 2.0 s through a per-row JSON function (a
Python stand-in for the C# one); a lookup equality took 474 ms. With the
fields in indexed columns: 1.6 ms and 0.11 ms.

## Where it shows up

- [`ItemQueryTranslator`](../src/Modules/Lists/PaperDotNet.Lists/Querying/ItemQueryTranslator.cs)
  (`FieldContains`)
- [`SqliteConnectionSetup`](../src/BuildingBlocks/PaperDotNet.Persistence.Sqlite/SqliteConnectionSetup.cs)
  (`pdn_json_contains`), [`SqliteDatabaseProvider`](../src/BuildingBlocks/PaperDotNet.Persistence.Sqlite/SqliteDatabaseProvider.cs)
  (containment index dropped)

## Possible approaches

- Promoted fields in typed columns and a table for multi-valued references
  ([item-and-permission-storage.md](../docs/item-and-permission-storage.md),
  decision 2; also [0005](0005-dynamic-json-fields-no-promotion-path-for-hot-columns.md)).

## Done (2026-09-29)

[ADR-0035](../docs/adr/0035-item-storage-and-permissions-at-scale.md) step 4. Indexed fields no longer parse the JSON: equality, ranges and sorting use the item column, and filters on
multi-value fields use the value table (`IN` for rare values, `EXISTS` for common ones). Fields that are not indexed
still parse the JSON on SQLite.
