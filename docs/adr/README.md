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
| [0018](0018-automation-workflowcore.md) | Automation: own rules engine, workflows on WorkflowCore (instead of Elsa) | Superseded by 0019 and 0024 |
| [0019](0019-workflows-on-wolverine.md) | Workflow runs resumed through Wolverine messages (replaces WorkflowCore) | Accepted (model changed by 0024) |
| [0020](0020-sync-api.md) | Comments and activity on the SDK; change log for delta, change subscriptions, `$batch` | Accepted |
| [0021](0021-mcp-and-sdks.md) | MCP server with the official C# SDK and our own tool contract; SDKs generated with Kiota | Accepted |
| [0022](0022-smart-folders.md) | Smart folders on the item query engine; OData aliases for relative values; keyword promotion keeps ids | Accepted |
| [0023](0023-item-mutators-and-event-reactions.md) | Item mutators before the save; events for everything after it; one message per subscriber | Accepted |
| [0024](0024-one-automation-model.md) | One automation model: rules and workflows merged; runs started through the outbox | Accepted |
| [0025](0025-reliable-runs-on-several-servers.md) | Reliable automation runs and folder moves on several servers (leases, recovery, atomic hand-offs) | Accepted |
| [0026](0026-live-events-across-servers.md) | Live events across servers with PostgreSQL LISTEN/NOTIFY | Accepted |
| [0027](0027-semantic-and-hybrid-search.md) | Semantic and hybrid search with page-level hits (passages, in-process vector index, reciprocal rank fusion) | Accepted |
| [0028](0028-template-packages.md) | Template packages (zip: template, JSON content, files) for content and export/import | Accepted |
| [0029](0029-papermerge-import.md) | Import from Papermerge through a converter that writes a PaperDotNet package; no direct export to its database | Proposed |
| [0030](0030-account-lifecycle-and-preferences.md) | Account lifecycle (anonymizing deletes, last administrator, ending access) and preferences with organization defaults | Accepted |
| [0031](0031-reverse-proxy-sign-in.md) | Sign-in through an authenticating reverse proxy: trusted direct peers only, headers only at /connect/authorize | Accepted |
