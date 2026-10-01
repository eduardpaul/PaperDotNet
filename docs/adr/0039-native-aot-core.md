# ADR-0039: The server as one Native AOT binary on .NET 11

- **Status:** Accepted (Identity, Lists, Audit, Workflows, Jobs, Workspaces, the extension host, Notifications, Collaboration, Notes, Tasks, Calendar, Taxonomy, Search, smart folders and Provisioning (templates, packages, export and import) ported; the other modules still to port, see
  `docs/aot-porting-plan.md`)
- **Date:** 2026-09-30
- **Changes:** [ADR-0007](0007-odata-for-item-queries.md) (OData stays as the query syntax, without ASP.NET Core OData),
  [ADR-0008](0008-wolverine-for-reliable-events.md) (Wolverine with code generated ahead of time),
  [ADR-0003](0003-tenant-resolution-and-isolation.md) / [ADR-0013](0013-openiddict-passkeys-rls.md) (no global query filters, no OpenIddict)

## Context

The server (`src/PaperDotNet.Host`) is framework-dependent ReadyToRun on .NET 10. It uses 500 MB and more of memory.
That blocks the main goal of the project: one small container that anyone can host. The biggest costs are the JIT, the
Roslyn compiler that Wolverine loads to compile its handlers at start, and the EF Core models and LINQ queries that are
built and compiled at run time for about twenty DbContexts.

Native AOT removes all of that: no JIT, no Roslyn, a compiled EF Core model and SQL generated ahead of time. The
question was which parts of the stack still work under AOT, and at what cost. We asked for a *best effort*: keep a
dependency if it works under AOT, otherwise take a standard that does, otherwise write a plain REST API.

## What we found (spike on .NET 11 RC 1, EF Core 11 RC 1, Wolverine 6.43)

| Part | Under Native AOT | Notes |
|---|---|---|
| **Wolverine** (durable local queues, EF Core outbox, SQLite storage) | ✅ works | Handlers must be generated ahead of time (`codegen write`, `TypeLoadMode.Static`); message JSON through a source-generated `JsonSerializerContext`. |
| **EF Core** (SQLite) | ✅ works, with rules | Needs the compiled model and **precompiled queries** (interceptors generated at publish). Experimental in EF Core 11. See the rules below. |
| EF Core **global query filters** | ❌ | Compiled models reject them ("query filters are not supported"). |
| EF Core **dynamic LINQ** (queries composed at run time) | ❌ | Only statically analysable queries are precompiled; others throw. |
| EF Core **migrations** at run time | ❌ | They need the design-time model. |
| EF Core, **several providers in one binary** | ❌ | Precompiled queries carry the SQL of one provider. |
| **ASP.NET Core OData** | ❌ | MVC controllers and dynamic LINQ. |
| **Microsoft.OData.Core URI parser** (`$filter`, `$orderby`) | ✅ works | ~6 MB of binary. |
| **OpenIddict** | ❌ | Not AOT-compatible. |
| ASP.NET Core **BearerToken** + Data Protection + `PasswordHasher` | ✅ works | In the shared framework. |
| **Kiota** C# runtime (`Microsoft.Kiota.Bundle` 2.1) | ✅ works | Published with Native AOT without a single warning. |
| Microsoft.AspNetCore.OpenApi | ✅ works | Every parameter type needs source-generated JSON metadata. |
| **Jint** 4.16 (workflow scripts, ADR-0037) | ✅ works | The interpreter, async/await, promises, JSON, and the time/memory/statement/recursion limits all work. Its .NET interop (wrapping CLR objects, delegates passed to `SetValue`) uses reflection: host functions are `ClrFunction`s over `JsValue`s and values cross as JSON, which the sandbox did already. About 8 MB of binary. |
| **Cronos** (cron schedules) | ✅ works | Pure parsing and arithmetic. |
| **Workflow engine** (ADR-0036) | ✅ ported | Plain C# over `JsonObject`. What blocked it was the same as elsewhere: global query filters, reflection-based JSON, composed queries, sealed entities. |

Memory of the first slice (tenants, users, tokens, lists, items with OData queries, events to an audit log), published
for linux-x64 with default settings: **46 MB binary, about 85 MB resident when idle, 115–140 MB during a burst of 2,000
parallel writes and 500 filtered reads.** With workflows and Jint: **56 MB binary, 94–96 MB idle, about 150 MB under the
same burst** (each write then also checks the workflow triggers). The first OpenAPI request adds about 8 MB. Workstation
GC was worse at idle (106 MB) than the default. The JIT build of the same code uses about 170 MB idle. GC tuning, SQLite memory
limits and concurrency caps saved 10–20 MB more; we left them out as not worth the complexity.

## Decision

1. **The server in `src/` becomes one Native AOT binary on .NET 11**, changed in place: the building blocks, the host
   and each module keep their projects. Modules are ported one at a time onto the rules below; nothing is kept just
   because it exists. **Modules still to port stay in `src/Modules` but out of the build** (not in `PaperDotNet.slnx`,
   not referenced by the host), and so do their tests (`tests/PaperDotNet.IntegrationTests/ToPort`), the unit,
   architecture and performance tests, the tools and the PostgreSQL projects. The web UI CI job is paused.
2. **Budget:** under 150 MB resident when idle and under 300 MB under load, checked on every push by
   `eng/aot-smoke.sh`, which publishes the binary and runs it.
3. **Plain defaults over tuning.** Default GC and runtime settings. Memory tricks (GC knobs, heap limits, request
   throttling) only when the budget needs them.
4. **SQLite first.** Because the SQL is precompiled per provider, PostgreSQL will be a separate build of the same code
   (its own compiled model, interceptors and schema scripts), not an option read at start.

### Rules for ported code

- **A ported module imports `src/Modules/AotModule.props`:** trim and AOT analyzers (`IsAotCompatible`), the Minimal API
  request delegate generator (in a class library `MapGet` otherwise falls back to reflection), and EF Core's
  compiled-model and query generation at publish (`Microsoft.EntityFrameworkCore.Tasks`; a library also needs a
  runtimeconfig for it). Building blocks set `IsAotCompatible`; the host sets `PublishAot`. Warnings are errors.
  Third-party trim warnings (EF Core, OData, Jint's .NET interop, Weasel) are reported at publish but do not fail it.
- **Each module keeps its own DbContext** with an `IDesignTimeDbContextFactory` (for EF Core's generation at publish)
  and registers it with `AddModuleDbContext<T>()`; the build's database (SQLite) comes from `IDatabaseProvider`.
  Modules reference `PaperDotNet.Persistence.Sqlite` for the design-time options: a build has one database.
- **JSON only through source generation:** each module exposes its `JsonSerializerContext` as `IModule.Json`
  (requests, responses, events, OpenAPI parameter types); the host combines them for HTTP and Wolverine.
- **EF Core queries must precompile:**
  - one LINQ expression from a `DbSet` property to the terminal operator, never composed over several statements
    (write two static queries instead of an `if`);
  - copy the DbContext and every value the query uses into locals first (EF Core cannot yet bind method, lambda or
    primary-constructor parameters, dotnet/efcore#35887);
  - entity classes are not `sealed` (the generated materializer tests for `IInjectableService`);
  - no `DateTimeOffset` in `ORDER BY` on SQLite (order by the time-ordered id);
  - no `Skip` and `Take` in one query: precompiled, both get the parameter `@p` and the offset takes the limit's
    value (page by keyset on the time-ordered id instead);
  - entities live in the DbContext's namespace (the generated interceptors import only that one).
- **Tenant isolation without query filters:** every query on a tenant-owned set filters on `TenantId` explicitly.
  `SaveChangesGuard` (PaperDotNet.Persistence) sets the tenant on new rows and refuses writes into another tenant. Every endpoint gets a
  tenant-isolation test.
- **Dynamic queries go to SQL, not LINQ.** List item filters keep the **OData syntax** (`$filter`, `$orderby`, `$top`,
  `$skiptoken`, `$count`), parsed by Microsoft.OData.Core against a model built from the list's fields. The parsed
  tree is translated to parameterized SQL (`IItemQueries`, `SqliteItemQueries` in the Lists module: the translator is
  specific to the item tables, so it stays with them). Values are always parameters; column names come from a fixed
  map, JSON paths from validated field names. Other modules query items through `IListItemStore` (Lists.Contracts).
- **Indexed fields** keep their typed columns (`Text1..10`, `Number1..10`, `Date1..10`) and the value table; column
  values are set by name through the EF model (no reflection), and the backfill writes columns with SQL in
  `IItemQueries` like other bulk writes.
- **Lists live in workspaces** (`/v1.0/workspaces/{id}/lists/…`, workflows likewise). Access comes from the
  workspace role through the list's `acl_entries` (role principals), checked per item scope; there is no
  tenant-wide list any more.
- **Schema:** each module's EF Core migrations live in `PaperDotNet.Migrations.Sqlite` (design time only).
  `eng/schema.sh` turns each one into SQL embedded in `PaperDotNet.Persistence.Sqlite` (`Schema/`), applied on start in
  order of migration id and recorded in `__EFMigrationsHistory` as EF Core would. The schema starts fresh: the ported
  modules have new migrations; databases of the .NET 10 server are not upgraded.
- **Events:** subscribers are Wolverine handlers named `*Subscriber` in a module's assembly, generated ahead of time
  with `eng/codegen.sh` into `src/PaperDotNet.Host/Internal/Generated` and committed. Each subscriber gets its own message (`MultipleHandlerBehavior.Separated`). Publish with
  `IOutbox.SaveChangesAsync(db, events)`.
- **Authentication:** first-party clients keep the OAuth 2.0 password and refresh-token grants on `/connect/token`.
  Tokens are ASP.NET Core bearer tokens (Data Protection), valid while the user's security stamp is current; personal
  access tokens (`pdn_…`) are looked up by hash. One authentication scheme (`PaperDotNet`) handles both. Permissions
  are the user's effective scopes (roles assigned to the user or its groups), checked on every request; a token
  carries scopes only when it is limited to them. Tenants live in the Identity module for now (the Tenancy module is
  still to port).
- **Workflows** (flow form; the activity contract is `PaperDotNet.Workflows.Contracts`): triggers `manual`, `itemAdded`, `itemUpdated`,
  `itemDeleted` (with an OData `condition`); activities `if`, `setVariable`, `script` (Jint), `end`, `fail`, and the
  actions `item.create` and `item.update` (`IWorkflowActivity`, registered with `AddWorkflowActivity<T>()`). Runs are
  driven by `ResumeRun` messages through the outbox; a run's own changes are one causation level deeper and stop
  starting workflows at depth 8. Script host functions are `ClrFunction`s only. Not ported yet: waits (approvals,
  delays, retries), schedules and date triggers, `forEach`, `event.raise`, the `steps` form, built-in workflows, and
  leases for several servers.
- **Background work names its tenant.** There is no ambient tenant outside a request: messages, operations
  (`OperationContext.Actor`) and recurring jobs (`ITenantRecurringJob.RunAsync(tenantId, …)`) carry the tenant and
  user explicitly. Operation payloads and results, and live event data, are JSON (`JsonNode`, or typed with a
  `JsonTypeInfo`), never `object` serialized by reflection.
- **Soft delete is explicit too:** `ISoftDeletable` entities are moved to the recycle bin by `SaveChangesGuard` when
  removed (and purged when removed again); queries filter on `DeletedAt == null` like on the tenant.
- **JSON options of a module's context do not apply over HTTP.** The host combines the modules' source-generated
  contexts into the HTTP serializer options, and each type is serialized with those options, not the ones in the
  context's `[JsonSourceGenerationOptions]`. Mark properties to omit when null with
  `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` (e.g. `@odata.nextLink`).
- **Enums are not stored** as enums: EF Core's compiled model calls `Enum.GetValues(Type)` for them. Use string
  constants (e.g. `RunStatus`).
- **`dotnet format` may add `[RequiresUnreferencedCode]`** as its fix for a trim warning. Never keep it: fix the call.
- **No C# 15 collection arguments (`[with(…)]`) in modules:** EF Core's query precompiler compiles each module again
  with its own, older Roslyn at publish, which fails on them. Use a constructor (`new(comparer)`) or the default comparer.
- **Extensions build like modules** (ADR-0014): an extension project imports `src/Extensions/Extension.props` (AOT
  analyzers, request delegate generator, EF Core compiled model and precompiled queries at publish). The host finds the
  extensions it references with its source generator (`ReferencedExtensions`); a build adds them as references
  (`-p:PaperDotNetExtensions=…`, which the AOT smoke test uses for the Invoices sample). Contributions name the tenant
  explicitly (`IExtensionState(tenantId, …)`, `ItemMutationContext.TenantId`, `Caller` in endpoints). Extension tables
  (`ExtensionDbContext`, prefix `ext_{id}_`) follow the module query rules; their migrations live in the extension's
  `.Migrations.Sqlite` project and their SQL in the extension's `Schema/` folder (`eng/schema.sh`), embedded and applied
  by the host after the modules' scripts (`SchemaScripts`).

### SDKs

Kiota stays: its C# runtime is AOT-clean, and the TypeScript SDK does not run in the server. The server's OpenAPI
document is `src/PaperDotNet.Host/openapi.json` (`eng/openapi.sh`). `sdk/` (C#, TypeScript and Python SDKs) still
describes the .NET 10 API; it is regenerated from the new document when the web UI moves to the AOT server.

### Search under Native AOT

Full-text search uses SQLite FTS5 tables over the search documents and passages, kept by triggers created in the
migration. Their row ids are the tables' declared integer keys, so they never change (SQLite may renumber implicit row
ids, e.g. on `VACUUM`). EF Core cannot precompile full-text or dynamically filtered queries, so the search SQL sits
behind `ISearchQueries` with one implementation per provider, like `IItemQueries`; sets of ids are one JSON parameter
read with `json_each`. Results are trimmed by permission scope: Lists tells Search what a user may read
(`IItemAccess.GetReadableAsync`), and a permission move refreshes the scopes of the moved documents.

### Templates and packages under Native AOT

Templates are XML (System.Xml with the embedded XSD, which is AOT-safe); every section names the tenant through
`TemplateContext.Actor`, and packages are zip files whose JSON documents are read and written as `JsonNode`. Imported
items keep their original created and changed stamps through `AuditOverrides`, a scoped service the save guard reads.
Export and import of whole workspaces or tenants run as operations (`/v1.0/portability`), with packages in blob storage
and a daily cleanup job.

### Still to port

Documents (upload, versions, OCR, page images, and their files in template packages), semantic and hybrid search, the
remaining extension points (workflow triggers and shipped workflows, MCP tools), built-in workflows in templates, MCP,
AI workflows,
sign-in in the browser (authorization-code flow, passkeys, OAuth client applications, reverse-proxy sign-in), the admin CLI and backups, the Papermerge import, PostgreSQL (its own build), workflow waits and
schedules, the web UI and the SDKs. Each follows the rules above and brings its tests back from `ToPort`.

## Consequences

- The memory goal is met with room to spare for more modules. Each module adds code (and so memory), so the smoke test
  runs on every push.
- EF Core's AOT support is experimental. We already work around five bugs (parameters, sealed entities, missing
  usings, nullable warnings in generated code, `Skip` with `Take`). Each is noted where it is worked around, to be removed when EF Core
  fixes it.
- Precompiled queries are generated only at publish. Tests run the JIT build (runtime model, LINQ compiled at run
  time), so query shapes that do not precompile only show up in the AOT job. `eng/aot-smoke.sh` runs locally too.
- Queries lose the safety net of global filters. Isolation tests per endpoint and the save interceptor replace it.
  PostgreSQL row-level security (ADR-0013) will be a second layer again in the PostgreSQL build.
- The idle budget started at 100 MB. With Jobs and Identity ported the server idled at 104 MB; the standard GC
  settings saved at most 6 MB (gen0 size), because most idle memory is code and runtime structures that grow with each
  module. We raised the idle budget to 150 MB and kept plain defaults, rather than tune the GC or make modules opt-in.
- The message store's SQLite connections (Weasel, through Wolverine) come with a 64 MB page cache each and 256 MB of
  memory-mapped I/O. Resident memory then grew with the database while subscribers worked off a burst (up to 570 MB).
  The host passes Weasel's settings with SQLite's own page cache (2 MB) and no memory-mapped I/O, as on the modules'
  connections; under load it stays near 220 MB. The smoke test measures for ten seconds after the burst.
- Ical.Net (RRULE, iCalendar) creates its types with `Activator.CreateInstance`. The host roots the assembly for the
  trimmer (`TrimmerRootAssembly`); a Native AOT spike and the smoke test check RRULE expansion across DST, VTIMEZONE
  serialization and loading.
- OpenIddict (authorization code flow, passkeys) and ASP.NET Core OData are not used any more. Other dependencies are
  not checked under AOT yet (the MCP SDK, PDF and OCR libraries). Each
  is tested when its module is ported, with the same best-effort rule: keep it if it works, otherwise find a standard
  that does, otherwise write the plain version.
- Until modules are ported, the product does less than the .NET 10 server did: the list under "Still to port" is the
  plan, and the web UI waits for it.
