# Architecture Decision Records

Short records of significant decisions: context, decision, consequences.
New decisions get the next number. Superseded records stay, marked as such.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith-single-binary.md) | Modular monolith in one binary, with admin CLI | Accepted |
| [0002](0002-staged-authentication.md) | Staged authentication: local JWT + API tokens first, OpenIddict in P1 | Accepted (stage 2 done: ADR-0013) |
| [0003](0003-tenant-resolution-and-isolation.md) | Finbuckle for tenant resolution, isolation enforced in EF Core | Accepted |
| [0004](0004-provider-specific-migrations-project.md) | PostgreSQL migrations in a separate project | Accepted |
| [0005](0005-apache-2-license.md) | Apache-2.0 as project license | Accepted |
| [0006](0006-phase-0-simplifications.md) | Phase 0 simplifications and deferrals | Accepted |
| [0007](0007-odata-for-item-queries.md) | OData libraries for item queries over dynamic fields | Accepted |
| [0008](0008-wolverine-for-reliable-events.md) | Wolverine for reliable events (outbox) | Accepted |
| [0009](0009-sqlite-default-postgresql-optional.md) | SQLite by default, PostgreSQL optional | Accepted |
| [0010](0010-recurring-jobs-scheduler.md) | Recurring jobs with a small scheduler + Cronos (instead of Quartz.NET) | Accepted |
| [0011](0011-permission-scopes.md) | Permission inheritance with security scopes | Accepted |
| [0012](0012-full-text-search.md) | Full-text search in the database (FTS5 / tsvector) with principal trimming | Accepted |
| [0013](0013-openiddict-passkeys-rls.md) | OpenIddict, passkeys, row-level security and Data Protection in the database | Accepted |
| [0014](0014-build-time-extensions.md) | Build-time extensions (no runtime plugin loading) | Accepted |
| [0015](0015-documents-on-the-sdk.md) | Documents built on the SDK, content-addressed file storage | Accepted |
| [0016](0016-phase-4-scope.md) | Tasks and Calendar on the SDK, CalDAV later | Accepted |
| [0017](0017-phase-5-scope.md) | Phase 5 scope; provisioning templates with per-module handlers | Accepted |
