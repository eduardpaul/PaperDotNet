# PaperDotNet

Extensible DMS + productivity platform (documents, tasks, calendar) in .NET,
inspired by Papermerge and SharePoint lists/libraries.

- Vision and architecture: `docs/architecture-vision.md`
- **Feature catalog, user stories and roadmap: `docs/features.md`**
- **Technical approach (source of truth for tech decisions): `docs/technical-approach.md`**
- Papermerge feature catalog: `docs/papermerge-features.md`
- .NET building blocks (libraries/platform features to use): `docs/dotnet-building-blocks.md`
- Dependency license register and policy: `docs/dependency-licenses.md`
- Web frontend (screens, UX principles, slices): `docs/frontend.md`
- Raw ideas (to be mapped to features): `ideas/` (see `ideas/README.md`)

## Guiding principle

**Self-hosting and practicality first** (simplicity = practicality).
- **Minimal install:** one PaperDotNet container (SQLite on a volume), fully
  working, OCR included. PostgreSQL is optional.
- **Optional, off by default:** external IdP, S3, cache server, external
  search, AI providers, separate workers.
- **Libraries:** use mature in-process libraries rather than reinventing them
  (scheduling, OAuth, query parsing, PDF). Never hand-roll security, protocol
  or file-format code.
- **Own code:** only where it is small, core to the product, or no good
  library exists.

## Current scope

**Native AOT server (ADR-0039)**: the server in `src/` is one Native AOT binary on .NET 11 (budget: under 150 MB
idle, under 300 MB under load, checked by `eng/aot-smoke.sh`). Work happens in place in `src/`. Ported: Identity,
Lists, Audit, Workflows, Jobs (operations, recurring jobs, live events), Workspaces. Modules still to port stay in `src/Modules` out of the build (not in `PaperDotNet.slnx`);
port them one at a time, best effort: keep a dependency if it works under AOT, otherwise use a standard that does,
otherwise a plain REST implementation. Their tests wait in `tests/PaperDotNet.IntegrationTests/ToPort`.

**Web UI (Phase 8)** on the TypeScript SDK with React, TanStack and Tailwind
(ADR-0033, plan and screen map in `docs/frontend.md`) is paused until it moves to the AOT API (its CI job is off;
`sdk/` still describes the .NET 10 API). The UI is an SDK consumer: gaps are fixed in the API and regenerated, never
worked around in `web/`. No mobile app until the user says so.

## Decided

- Multitenancy from the start: shared DB, `TenantId` on every tenant-owned row. Under Native AOT there are no global
  query filters: every query filters on `TenantId`, and `SaveChangesGuard` refuses cross-tenant writes (+ PostgreSQL
  RLS when that build exists).
- Databases: **SQLite by default, PostgreSQL optional** (ADR-0009); under AOT each database is its own build, because
  EF Core precompiles SQL for one provider (ADR-0039: SQLite first). All data access through EF Core; provider packages
  only in `Persistence.Sqlite` / `Persistence.PostgreSql`; SQL of dynamic queries behind an abstraction with an
  implementation per provider (e.g. `IItemQueries`/`SqliteItemQueries`).
- Extensions run in-process first; remote extensions come later.
- Dependencies: MIT or Apache-2.0; BSD/PostgreSQL License/ISC allowed with
  notice (THIRD-PARTY-NOTICES). No GPL/AGPL/LGPL/MPL/SSPL/commercial.
  Check and record every new dependency in `docs/dependency-licenses.md`.

## Build & test

```bash
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet   # if the SDK was installed locally (.NET 11, global.json)
dotnet build PaperDotNet.slnx                               # warnings (incl. trim/AOT analyzers) are errors
dotnet format PaperDotNet.slnx --verify-no-changes
dotnet test --solution PaperDotNet.slnx                     # SQLite, JIT build
eng/aot-smoke.sh                                            # Native AOT publish, run, behavior and memory budget
eng/schema.sh add <Name>                                    # model change: migrations + their SQL (Persistence.Sqlite/Schema)
eng/codegen.sh                                              # subscriber/message change: Wolverine handlers (Host/Internal/Generated)
eng/openapi.sh                                              # endpoint change: src/PaperDotNet.Host/openapi.json
```

## Native AOT rules (ADR-0039)

- A ported module imports `src/Modules/AotModule.props` (AOT analyzers, request delegate generator, EF Core
  compiled model and precompiled queries at publish), has its own DbContext with an `IDesignTimeDbContextFactory`
  (and one in `PaperDotNet.Migrations.Sqlite`), registers it with `AddModuleDbContext<T>()`, and exposes its
  source-generated `JsonSerializerContext` as `IModule.Json`. The host lists it in `PaperDotNetHost.Modules`.
- EF Core queries must precompile: one expression from a `DbSet` to the terminal operator, DbContext and captured
  values copied into locals first, entities not `sealed` and in the DbContext's namespace, no enums stored (string
  constants), explicit `TenantId` in every query. Dynamic queries (item filters) are OData syntax translated to SQL by
  `IItemQueries` per provider, never dynamic LINQ; other modules use `IListItemStore` (Lists.Contracts).
- Workflows: activities implement `IWorkflowActivity` (Workflows.Contracts, `AddWorkflowActivity<T>()`); Jint host
  functions are `ClrFunction`s over JSON values only.
- Never keep `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` that `dotnet format` adds as a "fix": fix the call.
- Prefer plain defaults over memory tuning; only tune when `eng/aot-smoke.sh` is over budget.

## Code conventions

Ported modules follow the AOT rules above. The conventions below describe the full design; where they name things
that are not ported yet (item permissions, search, taxonomy, operations, extensions, live events, waits, …), they apply
when that part is ported.

- Modules live in `src/Modules/<Name>` with an `IModule`, a DbContext in its own
  schema, feature folders (endpoints + handlers together), and a `.Contracts`
  project only when other modules need it. Modules reference each other only
  via contracts; never reference database provider packages in modules.
- Tenant-owned entities implement `ITenantOwned`; every query filters on `TenantId` (no global query filters under
  AOT). Every new endpoint gets a tenant-isolation test.
- Auth: callers use OAuth access tokens (`/connect/token`, password and refresh-token grants); tests get tokens with
  `TestHost.SignInAsync`.
- Endpoints: Minimal APIs under `/v1.0`, `TypedResults`, `RequireScope(...)`,
  `ApiErrors` for problems, `Page.Create` for lists, ETags for mutable resources.
- IDs via `Ids.New()` (UUIDv7); time via `TimeProvider`.
- Events: every reaction to a saved change → `IntegrationEvent` + a Wolverine handler class named `*Subscriber`
  (idempotent, one message per subscriber, code generated ahead of time with `eng/codegen.sh`), published with
  `IOutbox.SaveChangesAsync(db, events)`. Changes made in reaction to an event carry its `Depth` + 1 (`ChangeActor`).
  Changing or rejecting an item write before the save (`IItemMutator`, ADR-0023) is not ported yet.
- Item access (ADR-0035): check `schema.Access.Level(item.ScopeId)` (404 below Read) and
  filter queries with `schema.Access.Filter(level)`; other modules use `IItemAccess`
  (Lists.Contracts). Permissions are `acl_entries` per scope (the list, or an item with unique
  permissions); workspace roles are principals (`WorkspaceRolePrincipals`). Caches of what a user
  may access carry `AccessCacheTags.Principals`; a `HybridCache` factory that queries tenant data
  must be called with `CancellationToken.None` (with a cancellable token it runs without the tenant).
- Searchable content → push `SearchDocumentData` through `ISearchIndex`
  (Search.Contracts) from an event subscriber, with the content's permission scope
  (`ScopeId`, ADR-0035: search trims by the caller's readable scopes); implement `ISearchSource` for reindexing. Text with pages goes in
  `Pages` (page hits, SRC-09); semantic search embeds passages automatically when
  `AI:Embeddings` is configured (ADR-0027). AI providers come from `PaperDotNet.AI`
  (`IEmbeddingGenerator`, Microsoft.Extensions.AI), off by default.
- Fields that lists filter, sort or group on at scale → `indexed: true` on the field (ADR-0035): item columns or the
  value table, kept current by `ListsDbContext`; never add ad hoc columns or JSON indexes for one field.
- Tags/classification → term ids from `ITermStore` (Taxonomy.Contracts) in
  `managedMetadata`/`keywords` fields; never store tag names as values.
- Long work → `IOperations.StartAsync` + `OperationHandler<T>` (202 + `/operations/{id}`);
  recurring work → `ITenantRecurringJob` + `AddTenantRecurringJob` (cron, UTC).
- Extensions (ADR-0014): compiled in, reference only `PaperDotNet.Extensions.Abstractions`;
  new extension points go on `IExtensionBuilder` and must be gated per tenant
  (`IExtensionState`). Guide: `docs/extensions.md`. Extension data: list items via
  `IListItemStore`, own tables via `ExtensionDbContext` (`ITenantOwned` entities,
  migrations in `{extension}.Migrations.Sqlite/.PostgreSql`). Extension rules are
  analyzers (PDN1xxx in `PaperDotNet.Extensions.Analyzers`); extension tests use
  `PaperDotNet.Extensions.Testing` (`ExtensionTestHost`).
- Documents, Tasks, Calendar and Notifications are built on the SDK only (ADR-0015, ADR-0016): add
  missing pieces to the SDK/contracts, never reference module implementations.
  An upload only stores the file and raises `document.added`; text, thumbnails, page images and OCR are built-in
  workflows per library (ADR-0038), so new document work is a workflow activity, never code in the upload.
  Binary content goes through `IBlobStore`; extra item text for search through
  `IItemSearchContributor`; client notifications through `ILiveEvents`
  (`/v1.0/me/events`, SSE; across servers via LISTEN/NOTIFY on PostgreSQL, ADR-0026); user notifications (inbox, webhook) through
  `INotificationSender` (Notifications.Contracts) with a deduplication key.
- Workflows (ADR-0036, before: automation, ADR-0019/0024): one model, workflows (trigger + condition + steps or a
  flow of nodes connected by outcome ports); activities implement `IWorkflowActivity` (safe to repeat with
  `ExecutionKey`, described by `InputSchema`/`OutputSchema`/`Outcomes`) and triggers are raised with
  `IWorkflowTriggers` (Workflows.Contracts); long waits are bookmarks: return `WorkflowActivityResult.Wait(kind, key)`
  and complete with `IWorkflowBookmarks.CompleteAsync`; runs are started and resumed with `ResumeRun`
  messages through the outbox (no workflow engine or durable execution framework). Product processes people should
  see or vary ship as built-in workflows (EVT-12), not hidden code. Workflow content lives in the module that owns the
  domain and uses only the SDK, the same extension points as extensions: `services.AddWorkflowActivity<T>()`,
  `AddWorkflowTrigger(…)`, `AddWorkflow(…)` (extensions: the same on `IExtensionBuilder`; `Scope = Library` for built-ins
  turned on per library); the engine only runs workflows. Workflows follow each other by events: `wf.{key}.completed`,
  `wf.{key}.failed` and `event.raise` (`wf.{key}.{event}`), never by code that calls another workflow. Build workflow features
  from workflow parts (waits with JSON data, run-again activities, built-in workflows), not tables or jobs of their own
  (e.g. batched AI: `ai.batch` waits + the "AI batch" workflow). Mapping data into lists is workflow JSON, not a new
  action: `item.update`/`item.create` with typed single tokens (`"total": "{step:read.json.total}"`) and `forEach`, or a
  `script` node (JavaScript in a sandbox, ADR-0037; samples/receipts-package). The script API is a contract of
  `@paperdotnet/client` (`runWorkflowScript`): change it in both, with a case in `sdk/typescript/test/scripts.test.mjs`. Code that reacts to an event
  and changes data should set `EventCausation.Depth` to the event's depth + 1 (loop protection).
- Group membership → `IUserDirectory` (`GetGroupIdsAsync`, `GetGroupMembersAsync`), which includes
  groups inside groups (ADR-0035); inside Identity, go through `GroupClosures`, never `GroupMembers` alone.
- User settings (time zone, languages, formats) → `IUserPreferences` (Identity.Contracts); never add
  per-module copies. Deleting a user or group publishes `PrincipalDeleted`: clean up references to it.
- Configuration must be portable (PRV, ADR-0017): a module with its own configuration
  implements `ITemplateHandler` (Provisioning.Contracts) for its template section,
  referencing other objects by name and honoring `TemplateContext.DryRun`.
- Web UI (`web/`, ADR-0033): API calls only through `@paperdotnet/client` with query keys from
  `web/src/api/keys.ts`; routes are TanStack Router files with typed search params (state in the URL);
  primitives in `components/ui` (Radix + Tailwind); format dates and numbers with `lib/format.ts`
  (user preferences); send `If-Match` on edits and handle 412; every screen gets a Playwright test.
- Record decisions as ADRs in `docs/adr/`.

## Ideas workflow

When the user says "Add idea: …":
1. Create `ideas/NNNN-short-title.md` from `ideas/_template.md` (next free number).
2. Add a row to the index in `ideas/README.md` and bump the example number.
3. Commit and push.

When ideas are reviewed, map them to feature IDs in `docs/features.md`
(add features + user stories as needed) and set the idea status to `mapped`.
