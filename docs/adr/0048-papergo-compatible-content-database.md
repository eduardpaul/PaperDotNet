# ADR-0048: PaperGo-compatible organization content database

Status: Accepted (2026-10-23)

## Decision

The content persistence model targets the exact development schema at PaperGo
commit `485be496d5c3b9077c9a6238cd8f11d3e4bffeda`. This extends ADR-0047's
feature parity decision with an explicit storage compatibility requirement.
SQLite remains the default; PostgreSQL is supported with the corresponding
PaperGo schema. One organization occupies one database, as in PaperGo.

`PaperDotNet.Content` contains provider-neutral EF Core mappings of all 64
reference tables and a transactional content lifecycle. Provider assemblies
own the two initial EF migrations and timestamp/locking differences. The
initial migrations execute the reviewed canonical DDL through EF's migration
SQL generator and command executor. This preserves constraints, partial
indexes, deferred PostgreSQL foreign keys, seeds, and JSON types that ordinary
EF convention-based generation would not reproduce exactly.

The installed version is PaperGo's SHA-256/base64 `schema_metadata` fingerprint.
Do not use `EnsureCreated`, normal modular migrations, or `Database.Migrate`
against an organization database: they can produce a different schema or an
extra EF history table. Initialization accepts an empty database or verifies
that the existing database has the expected table count and fingerprint. It
refuses unrecognized data instead of dropping or silently converting it.

The shared database has no .NET tenant discriminator, module schemas, RLS,
Wolverine tables, or EF history table. Organization isolation is the database
boundary. Existing multitenant module storage remains a separate database;
its query filters and RLS continue to apply there. It must never be attached
to a compatible content context or passed to `content-db`.

## Lifecycle

Every edit records a new immutable content snapshot tied to an immutable
effective schema snapshot. Optional retained history numbers are independent
of content revision numbers. History defaults off, and enabling it affects
future writes. Publishing collections keep draft and published projections;
automatically publishing collections keep a single published projection.
Folders remain immediately visible without publication/history entries.

Explicit publication exposes an already validated snapshot under its saved
schema. Unpublication withdraws that surface without deleting content or
history. Archive/unarchive checks the current lock version, changes discovery
state and audit/live events, and preserves content revisions, publication
pointers, location, filename reservations, and content modification stamps.

EF guards immutable revisions and publications in synchronous and asynchronous
tracked saves. As in PaperGo's current master, these are application guards;
the canonical DDL has no immutable-row triggers. Direct SQL and EF bulk update
APIs bypass them and are not supported mutation paths.

## Integration boundary

The host's `content-db` command runs before modular service/bootstrap setup.
It is a local database administrator interface, not an HTTP API: `--actor`
records attribution and does not impersonate a user or enforce their grants.
Consumers of `ContentStore` must supply `IContentAuthorization`, including
independent `read_draft` and `publish` checks.

The existing HTTP API, UI, search consumers and background jobs still use
module storage. They have not been rerouted to this database. This change
provides compatible EF storage and executable lifecycle operations; it does
not declare complete application interchangeability. An existing installation
needs an explicit data migration and consumer integration before switching
its web host. Database compatibility also does not provide a shared blob
store, extension runtime, deployment configuration, or authentication setup.

## Verification

Provider tests verify lifecycle transitions, authorization separation, stale
versions, rollback, exact field representations, both-surface unique keys,
keyword projections and counts. Contract tests compare all SQLite schema
objects to a fingerprint derived independently from the reference, verify
all table mappings and seed data, and reject mixed/unknown schemas.
The checked-in Go interoperability probe runs against the pinned reference
DAL in a disposable checkout and reads EF snapshots/projections, archives an
EF-created item, and records a schema that EF subsequently uses.

See [the operator guide](../papergo-database.md) for commands and limitations.
