# PaperDotNet

Extensible DMS + productivity platform (documents, tasks, calendar) in .NET,
inspired by Papermerge and SharePoint lists/libraries.

- Vision and architecture: `docs/architecture-vision.md`
- **Feature catalog, user stories and roadmap: `docs/features.md`**
- **Technical approach (source of truth for tech decisions): `docs/technical-approach.md`**
- Papermerge feature catalog: `docs/papermerge-features.md`
- .NET building blocks (libraries/platform features to use): `docs/dotnet-building-blocks.md`
- Dependency license register and policy: `docs/dependency-licenses.md`
- Raw ideas (to be mapped to features): `ideas/` (see `ideas/README.md`)

## Guiding principle

**Self-hosting and practicality first** (simplicity = practicality).
- **Minimal install:** one PaperDotNet container + PostgreSQL (+ a files
  volume), fully working, OCR included.
- **Optional, off by default:** external IdP, S3, cache server, external
  search, AI providers, separate workers.
- **Libraries:** use mature in-process libraries rather than reinventing them
  (scheduling, OAuth, query parsing, PDF). Never hand-roll security, protocol
  or file-format code.
- **Own code:** only where it is small, core to the product, or no good
  library exists.

## Current scope

**Backend API only.** Do not build web UI, frontend SDK or mobile app work
until the user says so. Design the API so a future UI has everything it needs.

## Decided

- Multitenancy from the start: shared DB, `TenantId` on every tenant-owned row,
  enforced via EF Core global query filters (+ PostgreSQL RLS).
- PostgreSQL only for now, but all data access through EF Core. No raw SQL
  outside the PostgreSQL persistence project; provider-specific features
  (full-text search, RLS, special indexes) go behind abstractions.
- Extensions run in-process first; remote extensions come later.
- Dependencies: MIT or Apache-2.0; BSD/PostgreSQL License/ISC allowed with
  notice (THIRD-PARTY-NOTICES). No GPL/AGPL/LGPL/MPL/SSPL/commercial.
  Check and record every new dependency in `docs/dependency-licenses.md`.

## Build & test

```bash
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet   # if the SDK was installed locally
dotnet build PaperDotNet.slnx                               # warnings are errors
dotnet format PaperDotNet.slnx --verify-no-changes
dotnet test --solution PaperDotNet.slnx                     # Testcontainers PostgreSQL
PAPERDOTNET_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres" dotnet test --solution PaperDotNet.slnx
dotnet tool restore && dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.PostgreSql -c <Module>DbContext -o Generated/<Module>
```

## Code conventions

- Modules live in `src/Modules/<Name>` with an `IModule`, a DbContext in its own
  schema, feature folders (endpoints + handlers together), and a `.Contracts`
  project only when other modules need it. Modules reference each other only
  via contracts; never reference Npgsql outside `Persistence.PostgreSql`.
- Tenant-owned entities implement `ITenantOwned`; never disable the `Tenant`
  query filter. Every new endpoint gets a tenant-isolation test.
- Endpoints: Minimal APIs under `/v1.0`, `TypedResults`, `RequireScope(...)`,
  `ApiErrors` for problems, `Page.Create` for lists, ETags for mutable resources.
- IDs via `Ids.New()` (UUIDv7); time via `TimeProvider`.
- Record decisions as ADRs in `docs/adr/`.

## Ideas workflow

When the user says "Add idea: …":
1. Create `ideas/NNNN-short-title.md` from `ideas/_template.md` (next free number).
2. Add a row to the index in `ideas/README.md` and bump the example number.
3. Commit and push.

When ideas are reviewed, map them to feature IDs in `docs/features.md`
(add features + user stories as needed) and set the idea status to `mapped`.
