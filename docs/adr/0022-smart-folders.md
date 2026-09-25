# ADR-0022: Smart folders on the item query engine

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Phase 5f brings:

- smart folders: rule-based views across lists (TAX-08);
- drop to classify (TAX-09);
- metadata navigation (TAX-10);
- keyword promotion (TAX-05);
- term set import and term sets from extensions (TAX-11).

Items of all types live in the lists engine, and their fields differ per list.

## Decision

- **Smart folders are stored in the Lists module.** They run on the existing
  item query engine.
  - A folder is a JSON definition: lists, templates, content types, terms, an
    OData filter and `groupBy`.
  - It is evaluated per list: the terms become conditions on the list's own
    term fields, including child terms (as in TAX-07). Lists where the filter
    does not apply are skipped.
  - Results are merged with a keyset on (`updatedAt`, `id`), so paging stays
    correct across lists.
  - There is no materialized membership; evaluation is capped at 100 lists per
    folder.
  - Access checks are the normal item access filters. Sharing a folder never
    grants access to items.
- **Relative values are OData parameter aliases** (`@me`, `@today`, `@next7Days`, …).
  - The OData parser resolves them; the translator maps the alias nodes to
    their values.
  - No hand-written token parsing. The aliases work in every item filter and
    view.
- **Drop to classify** derives the values from the definition itself: the
  folder's terms, the `eq` conditions joined with `and` in the filter (taken
  from the parsed OData tree), and the `groupBy` values of the sub-folder.
  - The values are written through `IListItemStore`, so validation,
    permissions, receivers and events apply.
  - Removing an item from a folder reverses the changes.
- **Group counts** use `GROUP BY` over the JSON value per list, optionally its
  year or month prefix. The counts are summed across lists.
  - This found a bug in both providers' JSON functions: their results had been
    declared non-null whenever the document was not null. So `eq null` checks
    on missing properties were optimized to false.
  - Fixed: the functions no longer take their nullability from the document.
- **Portable:** shared folders are a workspace template section
  (`urn:paperdotnet:smartfolders:1`). Lists are referenced by name, and terms
  as `Group/Set/Term/…` paths (new `ITermStore.GetTermPathsAsync` and
  `FindTermByPathAsync`).
- **Keyword promotion keeps ids.** A promoted keyword either moves into the
  target set with the same id, or is merged (`TermMerged` rewrites values).
  - It is flagged `AvailableAsKeyword`, so keywords fields keep accepting it.
  - Usage counts come from the search index (`ITermUsage`, Search.Contracts).
- **Term set templates** (`TermSetTemplate`, `ITermSetProvisioning`) serve both
  sources:
  - the CSV import, parsed with .NET's own `TextFieldParser` (no new
    dependency);
  - extensions, through `AddTermSet`, provisioned on enable and identified by
    key.

## Consequences

- A folder over many large lists runs one query per list. The cap and keyset
  paging keep this bounded; materialized membership can come later if needed.
- `groupBy` supports single-value fields only. Multi-value fields (such as
  keywords) are filtered by `terms` instead.
- "Today" is in UTC; per-user time zones can be added to the aliases later.
