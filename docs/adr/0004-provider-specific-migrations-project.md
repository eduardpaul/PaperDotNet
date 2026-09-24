# ADR-0004: PostgreSQL migrations in a separate project

**Status:** Accepted (2026-09-24)

## Context
EF Core migration snapshots reference provider annotations. Keeping them in
module projects would make every module depend on Npgsql and break the
provider-agnostic rule.

## Decision
All PostgreSQL migrations live in `src/Migrations/PaperDotNet.Migrations.PostgreSql`,
one folder per module under `Generated/`, each with its own history table in
the module schema. Design-time factories there build the contexts.

```
dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.PostgreSql -c <Module>DbContext -o Generated/<Module>
```

CI fails when a model has changes without a migration.

## Consequences
A second database provider would add a sibling migrations project; modules stay
unchanged.
