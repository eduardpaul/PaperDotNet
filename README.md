# PaperDotNet

An extensible document management and productivity platform for .NET:
documents (DMS), tasks and calendar on a SharePoint-style lists engine,
inspired by [Papermerge](https://github.com/papermerge/papermerge-core).
Self-hosted with just **one app container + PostgreSQL**.

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
cp deploy/.env.example deploy/.env      # set the passwords and signing key
docker compose -f deploy/docker-compose.yml up -d --build
curl -s -X POST localhost:8080/v1.0/auth/token \
  -H 'content-type: application/json' \
  -d '{"userName":"admin","password":"<ADMIN_PASSWORD>"}'
```

Admin commands run in the same image:

```bash
docker compose -f deploy/docker-compose.yml exec paperdotnet dotnet paperdotnet.dll tenant list
docker compose -f deploy/docker-compose.yml exec paperdotnet dotnet paperdotnet.dll tenant create --identifier acme --name "Acme" --host dms.acme.com
```

## Development

Requirements:
- .NET 10 SDK
- PostgreSQL 16+ (local, or Docker for the tests)

```bash
dotnet build PaperDotNet.slnx
dotnet run --project src/PaperDotNet.Host          # Development: localhost PostgreSQL, admin / admin-password-dev
dotnet test --solution PaperDotNet.slnx            # integration tests start PostgreSQL via Testcontainers
PAPERDOTNET_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres" dotnet test --solution PaperDotNet.slnx
```

Configuration comes from environment variables `PAPERDOTNET__Section__Key`.
See `src/PaperDotNet.Host/appsettings.json` for all settings.

Add a migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.PostgreSql -c <Module>DbContext -o Generated/<Module>
```

## Repository layout

```
src/
  PaperDotNet.Host/             composition root, CLI, Dockerfile entry point
  PaperDotNet.ServiceDefaults/  telemetry, health, resilience
  BuildingBlocks/               Abstractions, Api conventions, Persistence, Persistence.PostgreSql
  Modules/                      Tenancy, Identity, Workspaces (+ Contracts)
  Migrations/                   PostgreSQL migrations of all modules
tests/                          unit, architecture and integration tests
deploy/                         docker-compose for self-hosting
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
