# ADR-0004: PostgreSQL migrations in a separate project

**Status:** Accepted (2026-09-24)

## Context
EF Core migration snapshots reference provider annotations. Keeping them in
module projects would make every module depend on Npgsql and break the
provider-agnostic rule.

## Decision
Migrations live in one project per provider (ADR-0009):
`src/Migrations/PaperDotNet.Migrations.Sqlite` and
`src/Migrations/PaperDotNet.Migrations.PostgreSql`, one folder per module under
`Generated/`, each module with its own history table. Design-time factories
there build the contexts. Every model change needs a migration in **both**.

```
dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.Sqlite -c <Module>DbContext -o Generated/<Module>
dotnet ef migrations add <Name> -p src/Migrations/PaperDotNet.Migrations.PostgreSql -c <Module>DbContext -o Generated/<Module>
```

CI fails when a model has changes without a migration.

## Consequences
Adding a provider adds a sibling migrations project; modules stay unchanged.
