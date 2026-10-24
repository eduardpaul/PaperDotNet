# PaperGo-compatible database through Entity Framework

The `PaperDotNet.Content` persistence engine and `content-db` commands use the
same SQLite/PostgreSQL schema as PaperGo main at
[`485be49`](https://github.com/eduardpaul/PaperGo/tree/485be496d5c3b9077c9a6238cd8f11d3e4bffeda).
All 64 tables are mapped in EF Core. The reference schemas are embedded as
explicit EF migrations, preserving their checks, foreign keys, indexes,
JSON storage, seed rows and schema fingerprint. Provider-specific operations
stay in the existing provider assemblies. No additional NuGet packages are
required.

The authenticated `/v1.0/content` API and the **Content** screen use a compatible
organization database for each tenant. Existing module storage remains separate
for identity and platform applications. Existing workspaces switch through the
explicit preview/import flow. Each compatible database belongs to one
organization, matching PaperGo. See [ADR-0048](adr/0048-papergo-compatible-content-database.md).

## Application use

Open **Content** from the sidebar. Create a workspace, then a list or document
library. Collection settings control publishing, retained metadata history,
fields and independent resource grants. Use **Drafts / latest** to edit and
publish; ordinary readers browse **Published**. Active, archived and all filters
apply before paging. **All collections** browses/searches the selected surface
across the organization, with permission checks before paging. Selecting an item
shows the schema saved with that revision, downloads, metadata history and restore.

The TypeScript SDK's `contentClient` helper retains integer/decimal precision:
field numbers cross the HTTP boundary as strings. Stored JSON and indexes keep
the exact PaperGo representations. Generated C#, TypeScript and Python request
builders cover the same endpoints. Changes require the current quoted `If-Match`
version (428 if missing, 412 if stale).

SQLite organization files live at `Storage:DataPath/content/{tenant-id}.db`, or
`Content:DataPath`. PostgreSQL uses `pdn_content_{tenant-id}` databases owned by
the application role. Pre-provision those databases, or configure
`Content:ProvisioningConnectionString` with permission to create databases owned
by that role. Keep this provisioning credential server-side. Ordinary content
queries and writes use the application connection.

## Existing workspace migration

As an organization administrator, choose **Import workspace**, select a source
workspace and review its preview. The API equivalents are
`GET /v1.0/content/migrations/{workspace-id}/preview` and
`POST /v1.0/content/migrations/{workspace-id}` with its `sourceHash`.
A changed source requires a new preview. Unsupported schemas block import.

Import preserves resource and content-type identifiers, metadata, retained
history, file-version references, taxonomy and content grants. The copy commits
as one destination transaction; it never overwrites existing target resources.
Source saves and cutover share a provider lock. After cutover, legacy content
routes reject access and module readers/search exclude the migrated workspace;
tracked background source writes are rejected too. Use Content as the authority.

The source store never retained immutable historical schemas: imported historical
metadata uses the available schema. Comments, tasks, calendars, workflow runs,
preview/OCR artifacts and other module records are not migrated or connected to
Content. Their original records are retained; their original workspace screens
are unavailable after cutover. Migrate document/list workspaces only when this
boundary is appropriate. Full platform consumer integration remains tracked in
[the parity register](papergo-parity.md).

Installation backups include consistent snapshots of each organization's
compatible database as well as blob files. Restore requires a stopped server.
PostgreSQL backup/restore needs `pg_dump`/`pg_restore` at least as new as the server.
CLI databases created outside the configured organization paths need their own
backups. Snapshot reclamation, native workflow/event dispatch and rich indexed
search for compatible content remain separate integration work.

## Initialize and use

Build the host, then initialize a new database:

```bash
dotnet build src/PaperDotNet.Host
mkdir -p data
export PAPERDOTNET_CONTENT_CONNECTION_STRING="Data Source=$PWD/data/content.db"
dotnet run --project src/PaperDotNet.Host --no-build -- content-db init
dotnet run --project src/PaperDotNet.Host --no-build -- content-db workspace --name "Organization"
```

Use the returned workspace ID to create a publishing list, then use its ID
and current version to record a schema:

```bash
dotnet run --project src/PaperDotNet.Host --no-build -- content-db collection --workspace WORKSPACE_ID --name Records --publishing
dotnet run --project src/PaperDotNet.Host --no-build -- content-db schema --collection COLLECTION_ID --version 1 --input tools/papergo/schema.example.json
dotnet run --project src/PaperDotNet.Host --no-build -- content-db create --parent COLLECTION_ID --input tools/papergo/item.example.json
```

The new item is a draft. Read or publish it using the returned item ID and
version; reload the current version after each command:

```bash
dotnet run --project src/PaperDotNet.Host --no-build -- content-db read --id ITEM_ID --surface head
dotnet run --project src/PaperDotNet.Host --no-build -- content-db publish --id ITEM_ID --version CURRENT_VERSION
dotnet run --project src/PaperDotNet.Host --no-build -- content-db read --id ITEM_ID
dotnet run --project src/PaperDotNet.Host --no-build -- content-db archive --id ITEM_ID --version CURRENT_VERSION
dotnet run --project src/PaperDotNet.Host --no-build -- content-db browse --collection COLLECTION_ID --archive Archived
dotnet run --project src/PaperDotNet.Host --no-build -- content-db unarchive --id ITEM_ID --version CURRENT_VERSION
```

`edit --id ... --version ... --input ...` replaces the supplied item's values
and records a new revision; it does not change a published snapshot in a
publishing collection. `unpublish` withdraws the published surface. Use
`configure --id COLLECTION_ID --version CURRENT_VERSION --publishing
--history major --max-versions 50` to retain future metadata history.
`history --id ITEM_ID` returns retained versions and their frozen schemas;
`restore-revision --id ITEM_ID --version CURRENT_VERSION --number N` validates
historical metadata under today's schema and records a new revision, retaining
the current blob. History defaults off and has separate numbering from content
revisions. A stale version exits with code 3.

For PostgreSQL, set the environment variable to an Npgsql connection string
and add `--provider postgresql` to each command. Initialization creates tables
in the connection's current schema and requires DDL permission. Normal content
writes require ordinary table permissions. SQLite uses foreign keys, WAL,
FULL synchronous durability, a 30-second busy timeout and Go-compatible date
serialization. PostgreSQL writers lock the same workspace row as PaperGo.

These are **local administrator commands** for someone with database access.
`--actor` controls attribution; it does not log in or enforce that user's
permissions. Application integrations must provide `IContentAuthorization` to
`ContentStore`; `read`, `read_draft`, `write`, `publish` and `manage` remain
separate actions. No unauthenticated network endpoint is introduced.

## Storage and lifecycle contract

- Content/schema revisions and publication records are immutable through EF
  tracked saves. Each revision retains its schema, content type, JSON payload,
  keywords, author and optional blob. Restoring records another revision.
- Explicit publishing reuses the saved schema, even after the current schema
  gains required fields. Typed values and keywords are projected separately
  for each surface; unique keys cover both surfaces.
- Without publishing there is one `published` surface, with head and published
  pointers equal. Enabling publishing creates head projections; disabling it
  publishes outstanding heads and removes head projections atomically.
- Folders are immediately visible and have no metadata history/publications.
- Archive/unarchive changes lock version and archive stamps, writes audit,
  domain and live-change records, and preserves content stamps, revisions,
  pointers, location and filename reservations. Repeating the same state is a
  no-op after checking the supplied version. Direct reads still work;
  ordinary browsing filters active content before pagination.
- Integers retain signed 64-bit precision; fixed decimals store canonical
  strings and scaled integer indexes; number/currency indexes use PaperGo's
  58-character exact numeric ordering. JSON is TEXT on SQLite and JSONB on
  PostgreSQL. Schema snapshots retain reference field/type JSON names.

## Verification and reference upgrades

Verified on 2026-10-23: all 16 content/schema tests passed on each provider,
including restore, index rebuilds, blob/filename selection and cross-field rules.
The solution builds with zero warnings/errors; all 110 architecture checks and
270 existing unit tests pass. The actual pinned Go DAL round trip also
passed on SQLite and PostgreSQL. These checks cover the storage engine and
commands; they do not certify the existing web application as interchangeable.

```bash
dotnet test --project tests/PaperDotNet.Content.Tests
PAPERDOTNET_TEST_PROVIDER=postgresql dotnet test --project tests/PaperDotNet.Content.Tests
python3 tools/papergo/generate.py ../PaperGo
```

The generator refuses an unreviewed commit or schema hash. It uses Python's
SQLite metadata to regenerate EF row mappings and provider mappings, and
copies the canonical DDL without changing it. `reference.json` pins both SQL
hashes and the SQLite schema-object hash. Initialization verifies the installed
fingerprint and table count; it does not attempt a full schema-drift repair.

To run the actual Go DAL round trip, use Go 1.27 and a disposable checkout:

```bash
git -C ../PaperGo worktree add --detach /tmp/papergo-interop 485be496d5c3b9077c9a6238cd8f11d3e4bffeda
mkdir -p /tmp/papergo-interop/cmd/paperdotnet-interop
cp tools/papergo/interop.go /tmp/papergo-interop/cmd/paperdotnet-interop/main.go
(cd /tmp/papergo-interop && go build -o /tmp/papergo-probe ./cmd/paperdotnet-interop)
python3 tools/papergo/check-interop.py --go-probe /tmp/papergo-probe
```

For PostgreSQL, point `PAPERDOTNET_CONTENT_CONNECTION_STRING` (Npgsql) and
`PAPERGO_INTEROP_CONNECTION` (Go URL/DSN) at the same isolated empty test
database and add `--provider postgresql` to the Python command. The probe reads
EF-created revisions, schemas, typed indexes and both surfaces with Go, then
archives the item and records another schema with Go. EF reads that state,
edits against the Go schema, and unarchives it.

The current Go development schema enforces immutable rows in application code,
not triggers. Direct SQL and EF `ExecuteUpdate`/`ExecuteDelete` bypass the
tracked-save guards; use lifecycle operations. Collection-level schema commands reject edits to field definitions or content
types shared with another collection, so they cannot leave its schema stale;
use fresh IDs or a coordinated catalog migration for those edits.
The local commands do not provision the platform directory or enforce HTTP
scopes. Snapshot/blob reclamation, native event/workflow dispatch, rich indexed
search and delta/package integration remain separate work. The authenticated
application provides file uploads/downloads, resource grants, migration and UI.
Both applications also need the same blob-store layout and coordinated schema
upgrades before sharing a production installation. A matching development
fingerprint does not promise compatibility with every PaperGo release.
