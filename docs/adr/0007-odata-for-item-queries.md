# ADR-0007: OData libraries for item queries over dynamic fields

**Status:** Accepted (2026-09-24)

## Context
List items have user-defined fields stored in one `jsonb` document per item
(ADR-0003, technical approach §6). Clients need Graph-style `$filter`,
`$orderby`, `$top`, `$skiptoken`, `$count` and `$select`. The choice was
between our own parser and the OData libraries; the user chose OData.

## Decision
- Use the OData stack (`Microsoft.AspNetCore.OData`, which brings
  `Microsoft.OData.Core`/`Edm`, all MIT) for **parsing and validation**:
  an EDM model is built at runtime from the list's effective fields
  (`ItemEdmModel`), and `ODataQueryOptionParser` produces the filter/orderby
  trees. Unknown fields, wrong literal types and syntax errors are rejected
  by the standard parser with OData error messages.
- **Translation** of those trees to LINQ is ours (`ItemQueryTranslator`),
  because OData's `ApplyTo` needs compile-time CLR types:
  - field values are read via `JsonDocument.RootElement.GetProperty(…)`,
    which Npgsql translates to `->>` with casts;
  - equality, `in` and `any` use JSON containment (`@>`) through the
    provider abstraction `IJsonQueryFunctions`, so the GIN
    (`jsonb_path_ops`) index is used;
  - dates are stored in canonical text forms that sort correctly.
- Items look like Graph list items: top-level properties (`id`, `parentId`,
  `contentTypeId`, `createdAt`, …) and `fields/<name>`.
- Paging: keyset on the UUIDv7 id without `$orderby`, offset in the opaque
  `$skiptoken` with `$orderby`.

## Supported (1b)
`eq ne gt ge lt le and or not in`, `contains/startswith/endswith`,
`tolower/toupper`, `any` with `eq`/`or` on multi-value fields, `eq null`,
`$orderby` on single-value fields, `$count=true`, `$select=<field names>`,
`viewId=<saved view>`.

## Consequences
- `$expand`, `$batch` and `$metadata` are not exposed yet; the OData ASP.NET
  Core package is in place for them.
- A future database provider needs its own `IJsonQueryFunctions` and must
  translate `JsonElement.GetProperty` (or replace the translator).
