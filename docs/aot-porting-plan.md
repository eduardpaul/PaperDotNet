# Porting to the Native AOT server (ADR-0039)

Goal: every module of the .NET 10 server working in the Native AOT server. Order: dependencies first, then complexity
(S, M, L, XL). Each task is committed on its own; a module is done when it builds under the AOT rules, its tests from
`tests/PaperDotNet.IntegrationTests/ToPort` pass again (adapted), and `eng/aot-smoke.sh` passes.

| # | Size | Task | Status |
|---|---|---|---|
| T01 | S | Housekeeping: dev launch settings, GLM image, compose overrides, samples | done (samples come with T09) |
| T02 | S | Storage building block (`IBlobStore`, local disk) | done |
| T03 | S/M | Jobs: recurring tenant jobs (Cronos) and operations (202 + `/operations/{id}`) | done (with live events, `/v1.0/me/events`, and the tenant directory in Identity) |
| T04 | M | Identity parity: tenants admin, user lifecycle, groups (nested), directory, preferences, API tokens | done: roles and scopes, nested groups, user lifecycle, lockout, API tokens, preferences, organization, directory. The authorization-code flow, passkeys, OAuth client applications and reverse-proxy sign-in need `/connect/authorize` and move to T17 with the web UI |
| T05 | M | Workspaces with members and roles | done (workspace templates come with T13) |
| T06 | L | Lists parity 1: workspace-scoped API, content types, all field types, folders | done: lists and libraries in workspaces (access from workspace roles through `acl_entries`), content types, field types except `managedMetadata`/`keywords` (T12), folders, OData queries with `any()`, aliases and date arithmetic. List templates and `templateKey` come with T08, the Home libraries with T15, moving a folder into another permission scope with T08 |
| T07 | L | Lists parity 2: versions and history, recycle bin, item mutators, full item events | done: item versions (list, get, restore), the recycle bin (list, restore, purge) with a daily cleanup job, mutators (`IItemMutator` in DI), restored and purged events in the audit log. The audit log across modules (`/v1.0/auditLog` by entity type) comes with T16 |
| T08 | XL | Lists parity 3: permissions (ADR-0035), indexed fields, views, templates, smart folders, delta, bulk | |
| T09 | M | Extension SDK, extension host and generator | |
| T10 | S-M | Notes, Collaboration, Notifications | |
| T11 | M | Tasks and Calendar | |
| T12 | L | Taxonomy and Search (SQLite FTS5, optional semantic search) | |
| T13 | M | Provisioning and templates | |
| T14 | L | Workflows parity (waits, approvals, schedules, `forEach`, `event.raise`, `steps`, built-ins) and AI workflows | |
| T15 | XL | Documents (PDF libraries, page images, OCR) | |
| T16 | M | MCP, admin CLI, backup and restore, Papermerge import, the audit log across modules | |
| T17 | XL | PostgreSQL build, SDK regeneration, web UI (with the authorization-code flow, passkeys, OAuth client applications and reverse-proxy sign-in) | |

Done before this plan: building blocks, Identity (sign-in slice), Lists (slice), Audit (slice), Workflows (flow slice).
