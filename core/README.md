# PaperDotNet core (Native AOT, .NET 11)

The PaperDotNet server as one Native AOT binary: no JIT, no Roslyn at run time, a compiled EF Core model and
precompiled SQL. Decision and rules: [ADR-0039](../docs/adr/0039-native-aot-core.md). The .NET 10 application in
`src/` keeps working while modules move here one at a time.

**Budget:** under 100 MB resident when idle, under 300 MB under load. Checked on every push by `eng/aot-smoke.sh`.
Last measurement (linux-x64, default settings, with workflows): 56 MB binary, 94–96 MB idle, about 150 MB during 2,000
parallel writes and 500 filtered reads.

## What is in it

| Area | Endpoints |
|---|---|
| Tokens | `POST /connect/token`: OAuth 2.0 `password` (with optional `tenant`) and `refresh_token` grants |
| Users | `GET /v1.0/me`, `GET/POST /v1.0/users` |
| Lists | `GET/POST /v1.0/lists`, `GET/PATCH/DELETE /v1.0/lists/{listId}` (ETag / `If-Match`) |
| Items | `GET/POST /v1.0/lists/{listId}/items` with `$filter`, `$orderby`, `$top`, `$skiptoken`, `$count` (OData syntax); `GET/PATCH/DELETE …/items/{itemId}` |
| Audit | `GET /v1.0/audit` (from events delivered through the Wolverine outbox) |
| Workflows | `GET/POST /v1.0/workflows`, `GET/PATCH/DELETE /v1.0/workflows/{id}` (a new definition is a new version), `GET /v1.0/workflows/activities`, `POST/GET /v1.0/workflows/{id}/runs`, `GET /v1.0/workflow-runs/{runId}` |
| Other | `GET /health`, `GET /openapi/v1.json`, `paperdotnet healthcheck` |

Field types: `text`, `note`, `number`, `boolean`, `dateTime`, `choice`. Filters: `eq ne gt ge lt le`, `and or not`,
`in`, `contains startswith endswith`, `eq null`, on `fields/<name>`, `id`, `createdAt`, `updatedAt`, `createdBy`,
`updatedBy`.

### Workflows

A workflow has a trigger (`manual`, `itemAdded`, `itemUpdated` with optional `changedFields`, `itemDeleted`; `list` by
name), an optional OData `condition` on the item, `variables`, and a `flow` of nodes connected by outcome ports:

```json
{
  "trigger": { "type": "itemAdded", "list": "Invoices" },
  "condition": "fields/status eq 'new'",
  "variables": { "threshold": 100 },
  "flow": { "start": "check", "nodes": {
    "check":  { "activity": "if", "inputs": { "left": "{amount}", "op": "gt", "right": "{var:threshold}" }, "next": { "true": "script", "false": "small" } },
    "small":  { "activity": "item.update", "inputs": { "fields": { "status": "small" } } },
    "script": { "activity": "script", "inputs": { "code": "await items.create('Tasks', { title: 'Review ' + item.title }); return 1;" }, "next": { "done": "done" } },
    "done":   { "activity": "end" }
  } }
}
```

Activities: `if` (`left`/`op`/`right`, or an OData `filter` on the item), `setVariable`, `script` (JavaScript with
`items.get/query/create/update/delete`, `item`, `vars`, `steps`, `trigger`, `log`; writes are planned, then applied),
`end`, `fail`, `item.create`, `item.update`. Tokens: `{title}`, `{field:format}`, `{id}`, `{list}`, `{today:yyyy}`,
`{var:name}`, `{step:node.path}`, `{data:name}`.

## Layout

| Path | What |
|---|---|
| `src/PaperDotNet.Core` | Building blocks: tenancy guard (`CoreSaveChangesInterceptor`), caller, scopes, events and outbox on Wolverine, API conventions |
| `src/PaperDotNet.Core.Host` | The binary (`paperdotnet`): `CoreDb`, modules in folders (`Identity`, `Lists`, `Audit`, `Workflows`), SQLite in `Persistence/Sqlite`, JSON in `CoreJson`, Wolverine handlers in `Internal/Generated`, schema in `Schema/Sqlite` |
| `src/PaperDotNet.Core.Migrations.Sqlite` | EF Core migrations, design time only |
| `tests/PaperDotNet.Core.Tests` | Tests against a real host (JIT build) |
| `eng/` | `schema.sh`, `codegen.sh`, `openapi.sh`, `aot-smoke.sh` |
| `sdk/openapi.json` | The API description, input of the Kiota SDKs |

## Build, test, publish

```bash
cd core                                    # its own global.json (.NET 11 SDK)
dotnet build PaperDotNet.Core.slnx         # warnings (incl. trim/AOT analyzers) are errors
dotnet format PaperDotNet.Core.slnx --verify-no-changes
dotnet test --solution PaperDotNet.Core.slnx
eng/aot-smoke.sh                           # publish with Native AOT, run it, check behavior and memory
dotnet run --project src/PaperDotNet.Core.Host   # development: admin / ChangeMe!123 on http://localhost:5080
```

Native AOT needs `clang` and `zlib1g-dev` on Linux. Container: `docker build -t paperdotnet-core core`.

## When you change…

| Change | Then |
|---|---|
| An entity or `CoreDb` | `eng/schema.sh add <Name>` (migration + SQL script); commit both |
| A `*Subscriber` or an event it handles | `eng/codegen.sh`; commit `Internal/Generated` |
| A request, response or event type | Add it to `CoreJson` |
| An endpoint | `eng/openapi.sh`; add a tenant-isolation test |
| Any EF Core query | Follow the query rules in `CoreDb` (locals, one expression, explicit `TenantId`); `eng/aot-smoke.sh` proves it precompiles |
| A workflow activity | Implement `IWorkflowActivity`, register it with `services.AddWorkflowActivity<T>()`; safe to repeat with the same `ExecutionId` |
| Script host functions | Only `ClrFunction` over `JsValue`s, values as JSON (no .NET objects or delegates in the engine) |

`dotnet format` may "fix" a trim warning by adding `[RequiresUnreferencedCode]`; remove it and fix the call instead.
