# PaperDotNet core (Native AOT, .NET 11)

The PaperDotNet server as one Native AOT binary: no JIT, no Roslyn at run time, a compiled EF Core model and
precompiled SQL. Decision and rules: [ADR-0039](../docs/adr/0039-native-aot-core.md). The .NET 10 application in
`src/` keeps working while modules move here one at a time.

**Budget:** under 100 MB resident when idle, under 300 MB under load. Checked on every push by `eng/aot-smoke.sh`.
Last measurement (linux-x64, default settings): 46 MB binary, about 85 MB idle, 115–140 MB during 2,000 parallel
writes and 500 filtered reads.

## What is in it

| Area | Endpoints |
|---|---|
| Tokens | `POST /connect/token`: OAuth 2.0 `password` (with optional `tenant`) and `refresh_token` grants |
| Users | `GET /v1.0/me`, `GET/POST /v1.0/users` |
| Lists | `GET/POST /v1.0/lists`, `GET/PATCH/DELETE /v1.0/lists/{listId}` (ETag / `If-Match`) |
| Items | `GET/POST /v1.0/lists/{listId}/items` with `$filter`, `$orderby`, `$top`, `$skiptoken`, `$count` (OData syntax); `GET/PATCH/DELETE …/items/{itemId}` |
| Audit | `GET /v1.0/audit` (from events delivered through the Wolverine outbox) |
| Other | `GET /health`, `GET /openapi/v1.json`, `paperdotnet healthcheck` |

Field types: `text`, `note`, `number`, `boolean`, `dateTime`, `choice`. Filters: `eq ne gt ge lt le`, `and or not`,
`in`, `contains startswith endswith`, `eq null`, on `fields/<name>`, `id`, `createdAt`, `updatedAt`, `createdBy`,
`updatedBy`.

## Layout

| Path | What |
|---|---|
| `src/PaperDotNet.Core` | Building blocks: tenancy guard (`CoreSaveChangesInterceptor`), caller, scopes, events and outbox on Wolverine, API conventions |
| `src/PaperDotNet.Core.Host` | The binary (`paperdotnet`): `CoreDb`, modules in folders (`Identity`, `Lists`, `Audit`), SQLite in `Persistence/Sqlite`, JSON in `CoreJson`, Wolverine handlers in `Internal/Generated`, schema in `Schema/Sqlite` |
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
