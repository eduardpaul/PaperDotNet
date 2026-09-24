# ADR-0009: SQLite by default, PostgreSQL optional

**Status:** Accepted (2026-09-24). Supersedes "PostgreSQL only" (architecture
vision decision 4).

## Context
Self-hosting and practicality come first. A database server is the biggest
extra moving part for small installations. Larger installations still want
PostgreSQL (concurrency, row-level security, GIN indexes, pgvector).

## Decision
- **Two supported providers, same features:** `Database:Provider` = `Sqlite`
  (default) or `PostgreSql`. The default install is **one container** with a
  SQLite file under `Storage:DataPath` (`/data` in Docker).
- **Provider projects:** `PaperDotNet.Persistence.Sqlite` and
  `PaperDotNet.Persistence.PostgreSql` are the only projects that reference
  provider packages (architecture tests enforce this). Each has its own
  migrations project (`PaperDotNet.Migrations.Sqlite`, `.PostgreSql`).
- **Provider-neutral model rules:**
  - optimistic concurrency uses an app-managed `Version` column
    (`IsConcurrencyToken`, set/incremented by the interceptor) instead of
    PostgreSQL `xmin`;
  - JSON document columns are `string` properties marked `IsJsonDocument()`
    (PostgreSQL maps them to `jsonb`); queries use `JsonFunctions`
    (`Text`, `Number`, `Boolean`, `Contains`, `HasProperty`), translated by a
    method-call translator plugin in each provider;
  - index hints (`IsJsonContainmentIndex`) become GIN `jsonb_path_ops` on
    PostgreSQL and are dropped on SQLite.
- **Provider adjustments** (model customizers):
  - SQLite has no schemas: module tables are prefixed (`lists_items`), each
    module has its own history table; `DateTimeOffset` is stored as sortable
    integers; connections use WAL, a busy timeout, foreign keys and a
    registered `pdn_json_contains` function (PostgreSQL `@>` semantics).
  - PostgreSQL keeps schemas per module, `jsonb` + GIN, and translates
    containment to `@>`.
- **Tests and CI** run the complete suite on both providers.

## Consequences
- SQLite is single-node (one app instance); scale-out and row-level security
  require PostgreSQL. RLS (phase 1f) is PostgreSQL-only defense in depth; the
  EF tenant filters protect both providers.
- Future provider-specific features (full-text search: SQLite FTS5 vs
  PostgreSQL `tsvector`; vector search) go behind abstractions with an
  implementation for each provider.
- Wolverine (ADR-0008) and Quartz.NET both have SQLite and PostgreSQL storage.
