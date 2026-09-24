# Architecture Decision Records

Short records of significant decisions: context, decision, consequences.
New decisions get the next number. Superseded records stay, marked as such.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith-single-binary.md) | Modular monolith in one binary, with admin CLI | Accepted |
| [0002](0002-staged-authentication.md) | Staged authentication: local JWT + API tokens first, OpenIddict in P1 | Accepted |
| [0003](0003-tenant-resolution-and-isolation.md) | Finbuckle for tenant resolution, isolation enforced in EF Core | Accepted |
| [0004](0004-provider-specific-migrations-project.md) | PostgreSQL migrations in a separate project | Accepted |
| [0005](0005-apache-2-license.md) | Apache-2.0 as project license | Accepted |
| [0006](0006-phase-0-simplifications.md) | Phase 0 simplifications and deferrals | Accepted |
| [0007](0007-odata-for-item-queries.md) | OData libraries for item queries over dynamic fields | Accepted |
| [0008](0008-wolverine-for-reliable-events.md) | Wolverine for reliable events (outbox) | Accepted (1c) |
| [0009](0009-sqlite-default-postgresql-optional.md) | SQLite by default, PostgreSQL optional | Accepted |
