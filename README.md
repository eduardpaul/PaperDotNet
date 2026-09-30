# PaperDotNet

An extensible document management and productivity platform for .NET:
documents (DMS), tasks and calendar on a SharePoint-style lists engine,
inspired by [Papermerge](https://github.com/papermerge/papermerge-core).
Self-hosted with just **one container** (SQLite built in; PostgreSQL optional).

> **Status:** the server is being rebuilt as **one Native AOT binary on .NET 11**
> ([ADR-0039](docs/adr/0039-native-aot-core.md)): the .NET 10 server used 500 MB and more of memory; the AOT
> server has a budget of 100 MB idle and 300 MB under load, checked on every push. Modules move over one at a time;
> the ones still to port stay in `src/Modules` out of the build. See the [roadmap](docs/features.md#roadmap).

## What runs today (Native AOT server)

- **Platform:** multi-tenant (a `TenantId` on every row, checked on every query and save), SQLite, one container,
  events through a transactional outbox (Wolverine), OpenAPI at `/openapi/v1.json`, `/health`.
- **Identity:** tenants, users, OAuth 2.0 password and refresh-token grants on `/connect/token`, scopes.
- **Lists:** lists with typed fields (text, note, number, boolean, date-time, choice), items, OData queries
  (`$filter`, `$orderby`, `$top`, `$skiptoken`, `$count`), ETags, audit log.
- **Workflows:** flows of nodes on item and manual triggers with conditions, tokens, `item.create`/`item.update`,
  and JavaScript script steps in a sandbox.

Memory (linux-x64, default settings): a 54 MB binary, about 95 MB idle, about 150 MB during a burst of 2,000
parallel writes and 500 filtered reads.

**Still to port** (code in the repository, not in the build): documents and OCR, search, taxonomy, tasks, calendar,
notes, notifications, workspaces and permissions, collaboration, provisioning and templates, extensions, MCP, the
admin CLI and backups, PostgreSQL, the web UI and the generated SDKs (`sdk/`, which still describe the .NET 10 API).

## Quick start (Docker)

```bash
cp deploy/.env.example deploy/.env      # set the admin password
docker compose -f deploy/docker-compose.yml up -d --build
# API access token (password grant):
curl -s localhost:8080/connect/token -d grant_type=password -d username=admin -d password='<ADMIN_PASSWORD>'
```

## Development

Requirements: the .NET 11 SDK (see `global.json`); for publishing with Native AOT on Linux, `clang` and `zlib1g-dev`.

```bash
dotnet build PaperDotNet.slnx                       # warnings (including the trim and AOT analyzers) are errors
dotnet run --project src/PaperDotNet.Host           # Development: SQLite in ./data, admin / ChangeMe!123
dotnet test --solution PaperDotNet.slnx
eng/aot-smoke.sh                                    # publish with Native AOT, run it, check behavior and memory
```

| When you change… | Then |
|---|---|
| An entity or a module's DbContext | `eng/schema.sh add <Name>` (migrations and their SQL); commit both |
| A `*Subscriber` or a message it handles | `eng/codegen.sh` (Wolverine handlers); commit `src/PaperDotNet.Host/Internal/Generated` |
| An endpoint | `eng/openapi.sh`; add a tenant-isolation test |

Configuration comes from environment variables `PAPERDOTNET__Section__Key`; see
`src/PaperDotNet.Host/appsettings.json` for the settings.

## Repository layout

```
src/
  PaperDotNet.Host/             the Native AOT server: composition, generated Wolverine handlers, openapi.json
  BuildingBlocks/               Abstractions, Api, Messaging, Persistence, Persistence.Sqlite (schema scripts)
  Modules/                      Identity, Lists, Audit, Workflows (ported; AotModule.props) and modules still to port
  Migrations/                   SQLite migrations of the ported modules (design time)
tests/                          integration tests (ToPort/: tests of features still to port)
eng/                            schema.sh, codegen.sh, openapi.sh, aot-smoke.sh
deploy/                         docker-compose (single container)
docs/                           vision, features, technical approach, ADRs, licenses
ideas/                          raw ideas, mapped to features
```

## Documentation

- [Architecture vision](docs/architecture-vision.md)
- [Feature catalog, user stories and roadmap](docs/features.md)
- [Technical approach](docs/technical-approach.md)
- [Architecture decisions](docs/adr/README.md)
- [Dependency licenses](docs/dependency-licenses.md)

## License

[Apache-2.0](LICENSE). Third-party notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
