# PaperDotNet technical approach

**Status:** accepted; phase 0 implemented (2026-09-24). Decisions are recorded as [ADRs](adr/README.md). This replaces section 4 of
[architecture-vision.md](architecture-vision.md) as the source of truth for
technical decisions.

**Related docs:**
- [features.md](features.md): what we build
- [dotnet-building-blocks.md](dotnet-building-blocks.md): catalog of libraries
- [dependency-licenses.md](dependency-licenses.md): license policy

**Guiding principles**, in priority order:
1. Self-hosting and practicality: one app container (SQLite by default, PostgreSQL optional), everything else optional.
2. Security and tenant isolation by default.
3. Built-in .NET features first, then mature permissive libraries. Never hand-roll security, protocol or file-format code.
4. Provider-agnostic data access through EF Core. PostgreSQL-specific code is isolated.
5. Backend API first. The API must be complete enough for any UI; the web UI uses only the public SDK (ADR-0033).

---

## 1. Platform & build baseline

| Topic | Decision |
|---|---|
| Runtime | **.NET 10 LTS**, C# 14. Upgrade to the next LTS (.NET 12, Nov 2027). Skip STS releases unless a feature is needed |
| SDK pinning | `global.json` with `rollForward: latestFeature` |
| Shared build settings | `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, deterministic builds, `InvariantGlobalization=false` (we need culture-aware sorting/stemming) |
| Package versions | **Central Package Management** (`Directory.Packages.props`), plus transitive pinning. NuGet lock files in CI |
| Code style | `.editorconfig` with `dotnet format` in CI |
| Tests | xUnit v3 on **Microsoft.Testing.Platform** |
| Container | Multi-stage `Dockerfile` on the official `mcr.microsoft.com/dotnet/aspnet:10.0` image. Tesseract comes from distro packages. Runs as non-root, with a `HEALTHCHECK`. (SDK container publishing without a Dockerfile can't install Tesseract, so we use a Dockerfile) |
| Delivery | GitHub Actions on every push: build, test, format check, license check, SBOM, container image. Semantic versioning. Release notes from the changelog |
| Decisions log | Architecture Decision Records in `docs/adr/`, one short file per decision |

## 2. Architecture style: modular monolith with vertical slices

- **One deployable**, the `PaperDotNet.Host` ASP.NET Core app. It is split into
  **modules** with hard boundaries, one per bounded context:
  - Tenancy
  - Identity
  - Lists
  - Taxonomy
  - Files
  - Search
  - Events
  - Automation
  - Notifications
  - Sharing
  - Dav
  - Mcp
  - Extensions
  - Ai
- **Inside a module: vertical slices.** Each feature (e.g. `CreateItem`) keeps
  its endpoint, request/response, validation and handler together in one
  folder.
- **No MediatR and no generic repositories.** Endpoints call handlers
  directly (plain classes resolved from DI). The EF Core `DbContext` *is* the
  unit of work and repository.
- **Module boundaries:**
  - Each module has a small public **contracts** project: interfaces,
    DTOs and integration events. Its implementation is `internal`.
  - Other modules only reference contracts.
  - Synchronous queries across modules go through contract interfaces.
  - Side effects across modules go through **integration events** via the
    outbox.
  - Boundaries are enforced by **architecture tests** (ArchUnitNET, Apache-2.0).
- **Module registration:** each module exposes `IModule` with
  `AddServices(IServiceCollection, IConfiguration)` and
  `MapEndpoints(IEndpointRouteBuilder)`. The host composes them.
  Extensions use the same shape.
- **Built-in apps are extensions.** Documents, Tasks and Calendar are built
  only against the public extension SDK (see section 12).

### Solution layout

```
PaperDotNet.slnx
├─ src/
│  ├─ PaperDotNet.Host/                      # composition root, Program.cs, config, admin CLI (ADR-0001)
│  ├─ PaperDotNet.ServiceDefaults/           # OpenTelemetry, health checks, resilience defaults
│  ├─ BuildingBlocks/
│  │  ├─ PaperDotNet.Abstractions/           # IModule, ITenantContext, ICurrentUser, scopes, IDs
│  │  ├─ PaperDotNet.Api/                    # HTTP conventions: paging, ProblemDetails, ETags, scope authorization
│  │  ├─ PaperDotNet.Persistence/            # EF Core base: interceptors, filters, outbox, conventions
│  │  └─ PaperDotNet.Persistence.PostgreSql/ # the ONLY provider-specific project (FTS, RLS, jsonb, SKIP LOCKED)
│  ├─ Modules/
│  │  ├─ Lists/
│  │  │  ├─ PaperDotNet.Lists.Contracts/
│  │  │  └─ PaperDotNet.Lists/               # slices: Features/Items/CreateItem/…, Data/ListsDbContext
│  │  ├─ Tenancy/  Identity/  Taxonomy/  Files/  Search/  Events/  Automation/
│  │  └─ Notifications/  Sharing/  Dav/  Mcp/  Extensions/  Ai/
│  ├─ Sdk/
│  │  ├─ PaperDotNet.Extensions.Abstractions/   # public, SemVer-stable extension contracts
│  │  ├─ PaperDotNet.Extensions.Sdk/            # helpers, source generator, analyzers
│  │  └─ PaperDotNet.Extensions.Testing/        # test host for extension authors
│  └─ Migrations/PaperDotNet.Migrations.PostgreSql/  # provider-specific migrations of all modules (ADR-0004)
├─ extensions/
│  ├─ PaperDotNet.Documents/   PaperDotNet.Tasks/   PaperDotNet.Calendar/
├─ tests/
│  ├─ PaperDotNet.ArchitectureTests/
│  ├─ PaperDotNet.IntegrationTests/          # WebApplicationFactory + Testcontainers PostgreSQL
│  └─ <Module>.Tests/
├─ deploy/docker-compose.yml                 # paperdotnet + postgres (pgvector image)
└─ docs/  ideas/
```

## 3. API design (Graph-style)

| Topic | Decision |
|---|---|
| Framework | **Minimal APIs**, with route groups per module and `TypedResults` for compile-time checked responses |
| URLs | `/v1.0/...` resource paths: `/me`, `/workspaces/{id}/lists/{id}/items/{id}`, `/drives`-like library access for files, `/search/query`, `/subscriptions`, `/operations/{id}`. A `/beta` group for previews. Extensions live under `/v1.0/ext/{extensionId}/…` |
| Query options | **Decided (ADR-0007):** OData libraries parse and validate `$filter`/`$orderby` against an EDM model built per list at runtime; our translator turns the trees into LINQ over the `jsonb` fields (containment `@>` for equality, via `IJsonQueryFunctions`). `$top`, `$skiptoken`, `$count`, `$select`, saved views. `$expand`/`$batch` later |
| Paging | **Cursor (keyset) paging** with an opaque `@odata.nextLink`. No offset paging on large lists |
| Errors | RFC 9457 **ProblemDetails** everywhere (`AddProblemDetails`, exception handler), extended with a Graph-style `error.code`. Canceled before-events map to `409` or `422` with the handler's message |
| Validation | Built-in Minimal API validation (`AddValidation`, .NET 10) for request DTOs. Field-type validators for dynamic item fields |
| Concurrency | **ETags** on every resource from PostgreSQL `xmin` (row version). `If-Match` required on updates and deletes. Returns `412` on conflict |
| Idempotency | An `Idempotency-Key` header on POSTs that create things (uploads, items). Stored for 24 h |
| Long-running work | `202 Accepted` + `Location: /v1.0/operations/{id}`, plus an SSE notification when done |
| Real-time | **Server-Sent Events** (`TypedResults.ServerSentEvents`) per user, fed by the event pipeline. SignalR only when a UI needs two-way messaging |
| Files | Streamed uploads and downloads (no buffering), `Range` support, per-tenant size limits. Resumable uploads (tus) added in P7 for mobile |
| OpenAPI | Built-in `Microsoft.AspNetCore.OpenApi` (OpenAPI 3.1), with document transformers for extension endpoints. The document is committed and **snapshot-tested** (Verify), so API changes are always visible in review. Kiota generates the SDKs in CI |
| Versioning | URL segment (`v1.0`, `beta`). Breaking changes only in a new major version |
| Rate limiting | Built-in rate limiter, partitioned by tenant + client |
| Caching | `HybridCache` (in memory; optional Redis-protocol L2 such as Garnet) for schemas, term sets and permission lookups. Keys are prefixed by tenant, with tag-based invalidation |

## 4. Identity & security

- **Staged (ADR-0002):** P0 shipped ASP.NET Core Identity local accounts, JWT
  access tokens and API tokens. Since 1f, OpenIddict issues all OAuth tokens
  (ADR-0013); the device code flow and external IdPs are still to come.
- **Authentication server built in (P1):** **OpenIddict** + **ASP.NET Core Identity**
  (EF Core stores).
  - Flows: authorization code + PKCE, client credentials, refresh tokens,
    device code (for the CLI).
  - Local accounts support passwords and **passkeys** (Identity passkey
    support in .NET 10). MFA via TOTP.
  - External OIDC providers per tenant are optional (IAM-04).
- **API tokens / app passwords:**
  - random, prefixed tokens (`pdn_…`), stored as hashes
  - scoped and expiring
  - accepted by the API, WebDAV and CalDAV (basic auth with an app password,
    for DAV clients)
- **Authorization:**
  1. **Scopes** (from roles) are checked by policy on each endpoint. A custom
     `IAuthorizationPolicyProvider` creates policies for any scope, including
     scopes declared by extensions.
  2. **Resource permissions** (ACL with inheritance and sharing) are checked
     by a central `IPermissionService`, cached per user and tenant. The
     permission service is also used to filter queries and search.
- **Data protection:** the Data Protection key ring is stored in the database
  (`identity.data_protection_keys`, both providers).
- **OAuth / OIDC (1f, ADR-0013):** OpenIddict with Data Protection token format,
  persisted RSA server keys, tenant-owned clients, service accounts for client
  credentials, passkeys via ASP.NET Core Identity 10.
- **Row-level security (PostgreSQL):** `app.tenant_id` set per connection,
  policies generated from the EF model after migrations. Run the app as an
  ordinary role (superusers bypass RLS).
  Secrets such as webhook secrets, OIDC client secrets and extension settings
  are encrypted with it.
- **Secure defaults:**
  - HTTPS assumed behind a reverse proxy, with configurable forwarded headers
  - HSTS
  - CORS off unless configured
  - antiforgery for any cookie-based flow
  - strict upload limits
  - content-type sniffing on uploads
  - no stack traces in responses
  - redaction of sensitive log fields
- **Tenant-isolation tests are mandatory.** Every endpoint gets an automated
  test proving it can't read or write data of another tenant.

## 5. Multitenancy

| Layer | Mechanism |
|---|---|
| Resolution | **Finbuckle.MultiTenant** (decided, ADR-0003) resolves the tenant from custom host, host template, header (opt-in), token claim, then the default tenant (only when none was requested explicitly). The result is exposed as our own `ITenantContext`. Background work gets the tenant from the message, never from ambient state |
| EF Core | Every tenant-owned entity implements `ITenantOwned`. A **named query filter** `"Tenant"` is applied by convention (EF Core 10 named filters), next to `"SoftDelete"`. Normal code may disable `SoftDelete` but never `Tenant` (enforced by an analyzer/architecture test) |
| Writes | A `SaveChanges` interceptor stamps `TenantId` and rejects cross-tenant writes |
| Database | *(P1, ADR-0003)* PostgreSQL **row-level security** as defense in depth. A connection interceptor sets `app.tenant_id` on each connection, and policies compare it to `tenant_id` |
| Files | Blob keys are prefixed by tenant |
| Caches, search, events | Keys and indexes are always scoped by tenant |
| Self-hosted | One default tenant, same code path |

## 6. Data (EF Core 10 + SQLite or PostgreSQL)

> **ADR-0009:** SQLite is the default provider, PostgreSQL is optional, and
> every feature works on both. The points below apply to both unless marked;
> provider differences live only in the provider projects.

- **One database, one `DbContext` per module, one schema per module.**
  Migrations are kept per module and live next to it. They are applied by
  `paperdotnet migrate` (EF migration bundle) or optionally at startup for
  single-node self-hosting.
- **IDs:** UUIDv7 (`Guid.CreateVersion7()`), which is time-ordered and good
  for index locality. Strongly-typed ID records (e.g. `ItemId`) with EF value
  converters.
- **Time:** `DateTimeOffset` in UTC in storage. `TimeProvider` everywhere
  instead of `DateTime.Now`, for testability.
- **Fixed schema** (tenants, lists, content types, terms, ACLs…): normal
  entities. Structured settings (view definitions, list settings, manifests)
  are **EF Core 10 complex types mapped to JSON**.
- **Dynamic item fields** (user-defined columns) are stored in one **JSON
  document column** per item (`jsonb` on PostgreSQL):
  - EF complex types need compile-time CLR types, so dynamic fields are not
    complex types. They are mapped as a JSON document/string.
  - Queries use the provider-neutral `JsonFunctions`; each provider
    translates them (PostgreSQL: `jsonb` functions and `@>` with a GIN index;
    SQLite: `json_extract` and a registered containment function).
  - A new provider implements this translation, its model customizer, and
    search.
  - Values are validated by field-type handlers before saving.
- **Hierarchies** (taxonomy terms, later folders) use a **materialized path**
  column (`/id/id/`) with a normal index: subtree queries are `StartsWith`,
  which translates to an indexed prefix match on both providers. PostgreSQL
  `ltree` is not needed.
- **Tags** are term ids from the term store (Taxonomy module), stored in
  `managedMetadata` / `keywords` fields. Labels are resolved on write;
  filters on a term include its descendants; a merge rewrites stored ids in
  the background (`TermMerged`).
- **Reads:** `AsNoTracking` + projection to DTOs, split queries for
  collections, compiled queries on hot paths. **Writes:** `ExecuteUpdate` /
  `ExecuteDelete` for bulk work (term merge, retention). Lazy loading is
  never used.
- **Concurrency:** `xmin` as row version (Npgsql), surfaced as ETags.
- **Interceptors** (in `PaperDotNet.Persistence`) handle:
  - tenant stamping
  - audit columns
  - soft delete
  - outbox writes
- **Audit log:** an append-only `audit_log` table in every module schema
  (added by `ApplyPaperDotNetConventions`), written by the interceptor in the
  same transaction as the change: who, when, action (created, updated,
  deleted, restored, purged), entity and changed properties, plus the trace
  id. `[NotAudited]` excludes technical state. `GET /v1.0/auditLog` merges
  all modules by time.
- **Item versions** (LST-11) are snapshots written by `ItemWriter` in the
  same transaction when the list has versioning on; old versions are trimmed.
- **Recycle bin:** soft delete via `ISoftDeletable`; deleting an entity that
  is already soft-deleted purges it (and is audited as such).

## 7. Events, outbox & background processing

```
request → item mutators (sync, can modify/cancel; ADR-0023)
        → SaveChanges (data + outbox rows + audit, one transaction)
outbox dispatcher (BackgroundService)
        → woken by PostgreSQL LISTEN/NOTIFY, polling as fallback
        → claims rows with FOR UPDATE SKIP LOCKED   (multi-node safe)
        → Channel<T> per consumer group (bounded, back-pressure)
        → one message per subscriber: search indexer · webhooks · notifications · automation · SSE
```

- **At-least-once delivery.** Consumers are idempotent, backed by an inbox
  table for dedup where needed. Failures are retried with exponential
  backoff, then sent to a dead-letter table (visible to admins via API).
- **Decided (ADR-0008): Wolverine** provides this pipeline: EF Core
  transactional outbox with PostgreSQL message storage (same database, no
  broker), durable local queues, retries and dead-lettering. The diagram above
  describes the behavior; Wolverine implements the dispatch part.
- **Scheduling (ADR-0010):** delayed one-off work via Wolverine scheduled
  messages; recurring (cron) jobs via a small in-process scheduler with Cronos
  and an EF-backed state table (claim by optimistic concurrency, runs once per
  active tenant). Quartz.NET was dropped: its job store needs provider-specific
  scripts outside EF migrations.
- **Long-running operations** (EVT-06) are stored as `operations` rows with
  status and progress, driven by jobs or pipeline stages.

## 8. Files & document processing

> **ADR-0015.** 3a implemented: Documents module on the SDK, `IBlobStore` with
> local disk, content-addressed `stored_files`, `file_versions`, duplicate
> policy per library, orphan cleanup job. 3b implemented: processing as an
> operation (`documents.processFile`): PdfPig text, Tesseract CLI OCR (one call
> over all page images, `pdf` + `txt` output) stored as a new PDF version,
> PDFtoImage/SkiaSharp page images cached in the blob store, page texts per
> stored file, `IItemSearchContributor` feeds the item's search document.

- **Blob store abstraction** (`IBlobStore`):
  - **content-addressed** by SHA-256 under a tenant prefix, which gives exact
    dedup and integrity checks for free (DOC-10/11, idea 0014)
  - reference-counted, with orphans removed by a background job
  - **local file system by default**; S3-compatible storage optional (AWSSDK.S3)
- **Processing pipeline** (in-process, `Channel<T>` stages, bounded
  concurrency per stage and per tenant):
  1. **Sniff** the file type by magic bytes (own small code for PDF, TIFF,
     JPEG and PNG).
  2. **Normalize:** convert images to PDF with PDFsharp + SkiaSharp
     (+ LibTiff.Net for TIFF).
  3. **Extract** existing text with PdfPig. Skip OCR if the text layer is
     good.
  4. **Render** pages with PDFium (PDFtoImage/Docnet) to create thumbnails
     and OCR input.
  5. **OCR** with the **Tesseract CLI**, run via CliWrap, which is simpler
     and more robust than native bindings. It produces hOCR/text-only PDF,
     which PDFsharp merges into a new file version.
  6. **Index:** hand off to the search indexer.
- File processors from extensions plug into the same pipeline by MIME type.
- **Scale-out:** the same pipeline can run in a separate worker container
  (same image, `--role worker`), coordinated through the outbox and Quartz.

## 9. Search

> **SRC-05 (3b).** Search documents carry a language (e.g. `english`). On
> PostgreSQL the vector holds exact words (`simple`) plus stems in the row's
> language, and every query term matches its exact form or any language's stem
> before AND/OR/NOT are applied. SQLite's FTS5 index uses the `porter`
> tokenizer (English stems for all rows).

> **ADR-0012.** Implemented in 1e.3 for SQLite and PostgreSQL.

- `IFullTextSearch` (Persistence) with one implementation per provider:
  PostgreSQL stored weighted `tsvector` + GIN; SQLite FTS5 external-content
  table + triggers. Queries are parsed once (`FullTextQuery`) and rendered per
  provider.
- **One `search.documents` table** holds rows from all data types: tenant,
  workspace, container, content type, title, body, author, date; plus
  `document_principals` (security trimming) and `document_tags` (facets,
  hierarchical tag filters).
  - The **app** maintains it: owning modules push documents through
    `ISearchIndex` from their integration events. That keeps logic portable
    and lets extensions declare what is indexed (SRC-06).
- **Security trimming** in SQL: documents store reader principals (user,
  group, workspace member, workspace owner), matched against the caller's
  principals with an indexed `EXISTS`.
- **Vector search (P6, [ADR-0027](adr/0027-semantic-and-hybrid-search.md)):**
  documents are split into passages that keep page numbers (with their own
  full-text index for page hits). Embeddings come from `IEmbeddingGenerator`
  (Microsoft.Extensions.AI, any OpenAI-compatible endpoint, off by default),
  computed by a background job and stored with the passages. They are searched
  by an in-process index per tenant, the same on SQLite and PostgreSQL.
  **Hybrid ranking** fuses keyword and semantic candidates with reciprocal
  rank fusion. pgvector or an external engine can replace the in-process index
  for very large tenants.
- External engines (OpenSearch, Meilisearch, Qdrant) are optional providers
  later.

## 10. AI & MCP

- **`Microsoft.Extensions.AI`** abstractions everywhere (`IChatClient`,
  `IEmbeddingGenerator`).
  - Built with its middleware pipeline: function invocation, OpenTelemetry,
    distributed cache keyed by content hash, rate limiting.
  - Providers are configured per tenant: none, Ollama/ONNX local models, or a
    cloud provider.
  - Nothing requires AI.
- **Structured output** for extraction (AI-03): the JSON Schema of a content
  type (from field types via `JsonSchemaExporter`) is the response schema.
  Results go through normal validation and before-event handlers.
- **MCP server:** `ModelContextProtocol.AspNetCore` mapped at `/mcp`
  (streamable HTTP).
  - OAuth via OpenIddict (protected-resource metadata), so the user's
    permissions apply.
  - Generic tools (search, get/create/update items, list schemas) come from
    the lists engine. Extensions contribute tools.
  - Destructive tools require confirmation.
- **Agent Framework** only for later multi-step agents (inbox triage).

## 11. DAV protocols (WebDAV, CalDAV, CardDAV)

- One `Dav` module with shared WebDAV plumbing:
  - PROPFIND, REPORT, ETags, sync-collection (RFC 6578)
  - well-known discovery
  - app-password auth
- It maps to the same services as the REST API, so permissions, events and
  versioning behave identically.
- iCalendar/vCard mapping via **Ical.Net** (MIT). vCard via a small
  serializer, or a permissive library after a license check.
- Evaluate existing .NET WebDAV server libraries against the license policy
  before writing our own.

## 12. Extension runtime

> **ADR-0014:** extensions are compiled into the host (no runtime plugin
> loading), so JIT, ReadyToRun, trimmed and future Native AOT hosts all work.
> Author guide: [extensions.md](extensions.md).

- **Contracts:** `PaperDotNet.Extensions.Abstractions` (SDK, SemVer
  `ExtensionSdk.Version`) plus the module contracts it exposes (Lists, Jobs,
  Taxonomy, Search, Workspaces, Identity). Extensions never reference module
  implementations, persistence or messaging (architecture tests).
- **Discovery:** `[assembly: PaperDotNetExtension(typeof(X))]` + a **source
  generator** in the host that emits `ReferencedExtensions.Create()`; no
  scanning or reflection-based loading.
- **Manifest:** `extension.json` embedded as `paperdotnet.extension.json`,
  language-neutral (Node/Python sidecars in P7 use the same format), validated
  at startup; JSON Schema in `docs/schemas`.
- **Registration:** `IExtension.Configure(IExtensionBuilder)` registers field
  types, item mutators (sequence, content type/list filters, conditions; ADR-0023),
  event subscribers, integration events, recurring jobs and endpoints
  (`/v1.0/ext/{id}`); scopes come from the manifest. Every contribution is
  wrapped with a per-tenant enablement gate.
- **Tenant state:** `extensions.tenant_extensions` (enabled, settings);
  `/v1.0/extensions` to list, enable, disable and configure. Read per request.
- **Data (2c):** `IListItemStore` (Lists.Contracts) reads and writes items
  through the API pipeline, as the current user or `AsSystem()`. Own tables:
  `ExtensionDbContext` registered with `AddDbContext<T>()`, schema `ext_{id}`
  (passed to the context as an options extension, `ModuleSchema`), entities
  must be `ITenantOwned`; tenant filter, audit and RLS apply. Migrations in
  companion assemblies `{extension}.Migrations.{Sqlite|PostgreSql}`, found by
  name (`AddModuleDbContext(schema, migrationsAssemblyPrefix)`) and run by
  `DatabaseMigrator` with the host's.
- **Developer experience (2d):** `PaperDotNet.Extensions.Analyzers`
  (netstandard2.0, packed into the SDK package under `analyzers/dotnet/cs`)
  enforces tenant-owned entities, no disabled tenant filter, no raw SQL,
  registration, `TimeProvider` and `Ids.New()`. `PaperDotNet.Extensions.Testing`
  wraps `WebApplicationFactory` around the real host (temporary SQLite or a
  PostgreSQL connection string) with tenant, client and in-tenant helpers.
- **Out-of-process (P7):** host-supervised sidecars and remote webhooks, for
  code that should not be compiled into the host.

## 13. Observability & operations

- **ServiceDefaults project** (Aspire pattern):
  - OpenTelemetry traces, metrics and logs, exported over OTLP only when
    configured
  - health checks
  - `Http.Resilience` standard handler for all outgoing HTTP
- **Logging:** `[LoggerMessage]` source-generated structured logging, with
  tenant, user, extension and correlation id as scopes. Personal data is
  redacted.
- **Configuration:**
  - `PAPERDOTNET__Section__Key` environment variables
  - Options classes validated at startup (`ValidateOnStart`, source-generated
    validators)
  - Docker secrets supported via key-per-file
- **Aspire AppHost** for local development: PostgreSQL, the app, optional
  Garnet/SeaweedFS, and the dashboard. Not required to run in production.
- **Admin CLI** (System.CommandLine): migrate, tenants, users, reindex,
  backup/restore (extension install is a rebuild, ADR-0014).
- **Backup (PLT-12, 3d):** `paperdotnet backup [-o file]` writes one `.tar.gz`
  with `manifest.json`, a database snapshot (`IDatabaseBackup`: SQLite online
  backup API; PostgreSQL `pg_dump -Fc --no-owner --no-privileges`) and the
  stored files (no temp files, no cached page images). The snapshot comes
  first: stored files are immutable and content-addressed, so everything it
  refers to exists when the files are copied. Safe while the server runs.
  `paperdotnet restore <file> [--force]` (server stopped) checks the provider,
  refuses to replace data without `--force`, restores (`pg_restore --clean`),
  replaces the files and migrates. PostgreSQL row-level security is forced for
  the owner role, so the tools run with the session setting
  `app.maintenance=on` (via `PGOPTIONS`), which the policies accept; the app
  never sets it.

## 14. Testing strategy

| Level | Tooling | Notes |
|---|---|---|
| Unit | xUnit v3, `FakeTimeProvider` | Domain logic, field types, query parser |
| Integration | `WebApplicationFactory` + **Testcontainers PostgreSQL** | Real DB for JSON queries, full-text search, RLS and outbox. One container per test run; tests are isolated by tenant (a new tenant per test) instead of DB resets |
| Contract | Verify snapshots | OpenAPI document, manifest schema, event payloads |
| Architecture | ArchUnitNET | Module boundaries, no provider-specific code outside the PostgreSQL project, no disabling of the `Tenant` filter |
| Security | Integration tests | Tenant isolation and permission checks for every endpoint |
| End-to-end | `Aspire.Hosting.Testing` (optional) | App + worker role + DB |

## 15. Changes compared with earlier drafts

This reanalysis corrects or sharpens these points:

1. **Dynamic fields are not EF complex types.** Complex types need CLR types
   known at compile time. User-defined fields use a JSON document column plus
   a provider-specific query translator. Complex-type JSON mapping is used
   for *fixed* structured data.
2. **The search index is maintained by the app, not DB triggers.** Triggers
   are provider-specific and hide logic. An outbox-fed indexer is portable and
   extensible.
3. **The outbox design is concrete:** `SKIP LOCKED` claims and
   `LISTEN/NOTIFY` wake-ups, feeding Channels. This is multi-node safe with
   no broker.
4. **Storage is content-addressed**, so deduplication and integrity checks
   come built in.
5. **OCR uses the Tesseract CLI via CliWrap** instead of native bindings,
   which is more robust and easier to upgrade. OCRmyPDF was dropped for
   licensing reasons.
6. **Identity:** OpenIddict + Identity with passkeys, and app passwords for
   DAV clients.
7. **API details:** keyset paging, `xmin` ETags, idempotency keys, a
   long-running operations resource, SSE, and a snapshot-tested OpenAPI
   document.
8. **Engineering baseline:**
   - vertical slices, no MediatR, no generic repositories
   - Central Package Management
   - architecture tests
   - UUIDv7 IDs and `TimeProvider`
9. **One schema per module** in a single database, with migrations per
   module applied by a migration bundle or the CLI.

## New dependencies introduced here (license-checked)

| Package | License |
|---|---|
| CliWrap | MIT |
| TngTech.ArchUnitNET (+ xUnit v3 integration) | Apache-2.0 |
| OpenIddict.EntityFrameworkCore | Apache-2.0 |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | MIT |
| Quartz.Extensions.Hosting, Quartz.Serialization.SystemTextJson | Apache-2.0 |
| OpenTelemetry.Extensions.Hosting / Exporter.OpenTelemetryProtocol / Instrumentation.AspNetCore | Apache-2.0 |
| Npgsql.OpenTelemetry | PostgreSQL License (🟨 with notice) |
| Microsoft.Testing.Platform | MIT |
