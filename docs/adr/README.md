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
| [0011](0011-permission-scopes.md) | Permission inheritance with security scopes | Accepted (evaluation replaced by 0035) |
| [0012](0012-full-text-search.md) | Full-text search in the database (FTS5 / tsvector) with principal trimming | Accepted (trimming replaced by 0035) |
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
| [0029](0029-papermerge-import.md) | Import from Papermerge through a converter that writes a PaperDotNet package; no direct export to its database | Accepted |
| [0030](0030-account-lifecycle-and-preferences.md) | Account lifecycle (anonymizing deletes, last administrator, ending access) and preferences with organization defaults | Accepted |
| [0031](0031-reverse-proxy-sign-in.md) | Sign-in through an authenticating reverse proxy: trusted direct peers only, headers only at /connect/authorize | Accepted |
| [0032](0032-sdk-for-first-party-clients.md) | Generated SDKs good enough for our own frontend: complete OpenAPI, ETags in bodies, thin runtime (auth, SSE, paging, files), end-to-end tests | Accepted |
| [0033](0033-web-frontend.md) | Web frontend on the TypeScript SDK with React, TanStack Router/Query/Table and Tailwind, served by the host from the same origin | Accepted |
| [0034](0034-optional-glm-ocr.md) | Optional GLM-OCR image; Tesseract stays the default OCR engine | Accepted |
| [0035](0035-item-storage-and-permissions-at-scale.md) | Item storage and permissions at scale: scope ACL looked up by principal, nested groups, promoted field columns and a value table, scope-based search and fan-out | Accepted (implemented) |
| [0036](0036-workflows-as-the-core.md) | Workflows as the core: Elsa-shaped graph engine of our own (triggers incl. schedule, dates and document processed; AI activities; built-in and user workflows); Elsa re-checked and still not a dependency; no durable execution framework; workflows export with lists and libraries | Accepted |
| [0037](0037-scripts-in-workflows.md) | Scripts in workflows: the graph orchestrates, a sandboxed JavaScript step (Jint) does the data work with planned, resumable writes; Roslyn scripting rejected for tenant workflows; explicit item targets, pausing long runs, safer loops, concurrency per item | Accepted |
| [0038](0038-documents-composed-from-workflows.md) | Documents composed from workflows: upload only stores (`document.added`); text, thumbnails, page images and OCR are built-in workflows per library; workflows have keys and raise `wf.<key>.<event>` events other workflows follow | Accepted |
| [0039](0039-global-item-relationships.md) | Stable item identity, symmetric relationships across lists/workspaces and identity-preserving moves | Accepted (implemented) |
| [0040](0040-typed-item-relationships.md) | Taxonomy-backed predicates, directed graph edges, endpoint limits and graph-aware receipt workflows | Accepted (implemented) |
| [0041](0041-relationship-attributes-and-workspace-queries.md) | Flat relationship attributes, versioned patches and permission-trimmed workspace graph queries returning both endpoints | Accepted (implemented) |
| [0042](0042-ical-source-replication.md) | iCalendar source replication in Calendar, independent subscriptions, existing workflows and SDK 1.0 restore/activation capabilities | Accepted |
| [0043](0043-search-indexing-as-workflows-over-a-search-store.md) | Search indexing as workflows over a pluggable search store: metadata (typed item fields, `$filter`) from producers, file content through `search.*` activities and chunkers per library, `ISearchStore` backend in its own database, embeddings computed once | Proposed |
| [0044](0044-zvec-search-store.md) | zvec as an optional in-process search store: one collection per tenant, head and chunk rows with metadata columns, field columns from the dynamic schema, full-text/vector/hybrid queries in zvec, C API via LibraryImport, single server | Proposed |
| [0045](0045-generic-reverse-proxies.md) | Running behind general-purpose reverse proxies (Nginx Proxy Manager): a shared secret from the proxy, a sign-in path outside `/connect/`, forwarded headers only from known proxies, streams through buffering proxies, opt-in group sync, daily re-check and logout of proxy sign-ins, local sign-in that can be turned off | Accepted (implemented) |
| [0046](0046-reviewed-document-storage-optimization.md) | Reviewed document storage optimization | Accepted |
| [0047](0047-webdav-for-libraries.md) | WebDAV for libraries on a vendored FubarDev.WebDavServer (release/2.0, Minimal API endpoint): `/dav/{workspace}/{library}/…`, Basic auth with API tokens only under `/dav`, read-only first, any file type in libraries, writes and EF-backed locks later | Proposed |
