# PaperDotNet

An extensible document management and productivity platform for .NET:
documents (DMS), tasks and calendar on a SharePoint-style lists engine,
inspired by [Papermerge](https://github.com/papermerge/papermerge-core).
Self-hosted with just **one container** (SQLite built in; PostgreSQL optional).

> **Status:** phase 0 (foundation). A backend API only; no UI yet.
> See the [roadmap](docs/features.md#roadmap).

## What works today (phase 0)

- Multi-tenant from the start. Tenants are resolved by host, header or token;
  data is isolated by EF Core filters.
- Local accounts with JWT access tokens, and personal API tokens (`pdn_…`) with scopes.
- Users, groups, roles made of fine-grained scopes, and workspaces with members.
- Graph-style REST API (`/v1.0/...`):
  - keyset paging with `@odata.nextLink`
  - ETag / `If-Match` concurrency
  - RFC 9457 problem responses
- Operations:
  - OpenAPI document at `/openapi/v1.json`
  - health endpoints `/health/live` and `/health/ready`
  - OpenTelemetry, exported when OTLP is configured
- Admin CLI in the same binary: `migrate`, `bootstrap`, `tenant`, `user`.

## Quick start (Docker)

```bash
cp deploy/.env.example deploy/.env      # set the admin password (REQUIRE_HTTPS=false to try it over plain HTTP)
docker compose -f deploy/docker-compose.yml up -d --build
# or with PostgreSQL:
# docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.postgres.yml up -d --build
# Access token (first-party client, password grant); apps use authorization code + PKCE or client credentials.
curl -s localhost:8080/connect/token \
  -d grant_type=password -d client_id=paperdotnet -d scope="api offline_access" \
  -d username=admin -d password='<ADMIN_PASSWORD>'
```

Admin commands run in the same image:

```bash
docker compose -f deploy/docker-compose.yml exec paperdotnet dotnet paperdotnet.dll tenant list
docker compose -f deploy/docker-compose.yml exec paperdotnet dotnet paperdotnet.dll tenant create --identifier acme --name "Acme" --host dms.acme.com
```

## Development

Requirements:
- .NET 10 SDK
- Optional: PostgreSQL 16+ (or Docker) to run against PostgreSQL

```bash
dotnet build PaperDotNet.slnx
dotnet run --project src/PaperDotNet.Host          # Development: SQLite in ./data, admin / admin-password-dev
dotnet test --solution PaperDotNet.slnx            # SQLite
PAPERDOTNET_TEST_PROVIDER=postgresql dotnet test --solution PaperDotNet.slnx   # PostgreSQL via Testcontainers
```

Configuration comes from environment variables `PAPERDOTNET__Section__Key`.
PostgreSQL: `PAPERDOTNET__Database__Provider=PostgreSql` and
`PAPERDOTNET__ConnectionStrings__PaperDotNet=Host=…;Database=…;Username=…;Password=…`.
See `src/PaperDotNet.Host/appsettings.json` for all settings.

Add a migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.Sqlite -c <Module>DbContext -o Generated/<Module>
dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.PostgreSql -c <Module>DbContext -o Generated/<Module>
```

## Repository layout

```
src/
  PaperDotNet.Host/             composition root, CLI, Dockerfile entry point
  PaperDotNet.ServiceDefaults/  telemetry, health, resilience
  BuildingBlocks/               Abstractions, Api conventions, Persistence, Persistence.Sqlite, Persistence.PostgreSql
  Modules/                      Tenancy, Identity, Workspaces (+ Contracts)
  Migrations/                   SQLite and PostgreSQL migrations of all modules
tests/                          unit, architecture and integration tests
deploy/                         docker-compose (single container; PostgreSQL override)
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
