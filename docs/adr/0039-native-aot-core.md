# ADR-0039: A Native AOT core on .NET 11

- **Status:** Accepted (first slice implemented in `core/`)
- **Date:** 2026-09-30
- **Changes:** [ADR-0007](0007-odata-for-item-queries.md) (OData stays as the query syntax, without ASP.NET Core OData),
  [ADR-0008](0008-wolverine-for-reliable-events.md) (Wolverine with code generated ahead of time),
  [ADR-0003](0003-tenant-resolution-and-isolation.md) / [ADR-0013](0013-openiddict-passkeys-rls.md) (no global query filters, no OpenIddict in the core)

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

1. **A new core in `core/`**, on .NET 11, published as one Native AOT binary. It has its own solution, `global.json`,
   packages and CI job, so the .NET 10 application keeps building while features move over. Modules are ported one at a
   time onto the rules below. Nothing is copied from `src/` just because it exists there.
2. **Budget:** under 100 MB resident when idle and under 300 MB under load, checked on every push by
   `core/eng/aot-smoke.sh`, which publishes the binary and runs it.
3. **Plain defaults over tuning.** Default GC and runtime settings. Memory tricks (GC knobs, heap limits, request
   throttling) only when the budget needs them.
4. **SQLite first.** Because the SQL is precompiled per provider, PostgreSQL will be a separate build of the same code
   (its own compiled model, interceptors and schema scripts), not an option read at start.

### Rules for code in the core

- **Trim and AOT analyzers are on** (`IsAotCompatible` for libraries, `PublishAot` for the host), and warnings are
  errors. Third-party trim warnings (EF Core, OData, Weasel) are reported at publish but do not fail it.
- **JSON only through `CoreJson`** (source generation): requests, responses, events and OpenAPI parameter types.
- **EF Core queries must precompile:**
  - one LINQ expression from a `DbSet` property to the terminal operator, never composed over several statements
    (write two static queries instead of an `if`);
  - copy the DbContext and every value the query uses into locals first (EF Core cannot yet bind method, lambda or
    primary-constructor parameters, dotnet/efcore#35887);
  - entity classes are not `sealed` (the generated materializer tests for `IInjectableService`);
  - no `DateTimeOffset` in `ORDER BY` on SQLite (order by the time-ordered id);
  - entity namespaces are global usings of the host (the generated interceptors do not import them).
- **Tenant isolation without query filters:** every query on a tenant-owned set filters on `TenantId` explicitly.
  `CoreSaveChangesInterceptor` sets the tenant on new rows and refuses writes into another tenant. Every endpoint gets a
  tenant-isolation test.
- **Dynamic queries go to SQL, not LINQ.** List item filters keep the **OData syntax** (`$filter`, `$orderby`, `$top`,
  `$skiptoken`, `$count`), parsed by Microsoft.OData.Core against a model built from the list's fields. The parsed
  tree is translated to parameterized SQL by the provider (`IItemQueries`, `SqliteItemQueries`). Values are always
  parameters; column names come from a fixed map, JSON paths from validated field names.
- **Schema:** EF Core migrations live in a design-time project (`PaperDotNet.Core.Migrations.Sqlite`).
  `eng/schema.sh` turns each one into SQL that the host embeds and applies on start, recorded in
  `__EFMigrationsHistory` as EF Core would.
- **Events:** subscribers are Wolverine handlers named `*Subscriber`, generated ahead of time with `eng/codegen.sh`
  and committed. Each subscriber gets its own message (`MultipleHandlerBehavior.Separated`). Publish with
  `IOutbox.SaveChangesAsync(db, events)`.
- **Authentication:** first-party clients keep the OAuth 2.0 password and refresh-token grants on `/connect/token`.
  Tokens are ASP.NET Core bearer tokens (Data Protection). Permissions are scopes in the token.
- **Workflows** (ported from `src/Modules/Workflows`, flow form): triggers `manual`, `itemAdded`, `itemUpdated`,
  `itemDeleted` (with an OData `condition`); activities `if`, `setVariable`, `script` (Jint), `end`, `fail`, and the
  actions `item.create` and `item.update` (`IWorkflowActivity`, registered with `AddWorkflowActivity<T>()`). Runs are
  driven by `ResumeRun` messages through the outbox; a run's own changes are one causation level deeper and stop
  starting workflows at depth 8. Script host functions are `ClrFunction`s only. Not ported yet: waits (approvals,
  delays, retries), schedules and date triggers, `forEach`, `event.raise`, the `steps` form, built-in workflows, and
  leases for several servers.
- **Enums are not stored** as enums: EF Core's compiled model calls `Enum.GetValues(Type)` for them. Use string
  constants (e.g. `RunStatus`).
- **`dotnet format` may add `[RequiresUnreferencedCode]`** as its fix for a trim warning. Never keep it: fix the call.
- **One DbContext** (`CoreDb`) for the core, with entity configuration per module. SQLite has no schemas, and one
  compiled model keeps publishing simple.

### SDKs

Kiota stays: its C# runtime is AOT-clean, and the TypeScript SDK does not run in the server. The core writes its OpenAPI
document to `core/sdk/openapi.json` (`eng/openapi.sh`). The SDKs are generated from it when the web UI moves to the core.

## Consequences

- The memory goal is met with room to spare for more modules. Each module adds code (and so memory), so the smoke test
  runs on every push.
- EF Core's AOT support is experimental. We already work around four bugs (parameters, sealed entities, missing
  usings, nullable warnings in generated code). Each is noted where it is worked around, to be removed when EF Core
  fixes it.
- Precompiled queries are generated only at publish. Tests run the JIT build (runtime model, LINQ compiled at run
  time), so query shapes that do not precompile only show up in the AOT job. `eng/aot-smoke.sh` runs locally too.
- Queries lose the safety net of global filters. Isolation tests per endpoint and the save interceptor replace it.
  PostgreSQL row-level security (ADR-0013) will be a second layer again in the PostgreSQL build.
- The idle budget has little room left (94–96 MB of 100 with workflows). The next modules either fit in it, or the
  budget is revisited on purpose; memory tuning stays the last resort.
- OpenIddict (authorization code flow, passkeys) and ASP.NET Core OData stay out of the core. Other dependencies are
  not checked under AOT yet (the MCP SDK, the extension host, PDF and OCR libraries). Each
  is tested when its module is ported, with the same best-effort rule: keep it if it works, otherwise find a standard
  that does, otherwise write the plain version.
- The web UI and the SDKs still target the .NET 10 API until the core covers enough of it.
