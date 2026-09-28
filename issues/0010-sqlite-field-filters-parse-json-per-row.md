# 0010: On SQLite, every equality filter on a field parses the JSON of every row

- **Status:** confirmed
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
480 ms with `json_extract` and 2.1 s through a per-row JSON function (a
Python stand-in for the C# one); a lookup equality took 480 ms. With the
fields in indexed columns: 1.5 ms and 0.12 ms.

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
