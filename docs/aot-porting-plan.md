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
| T08 | XL | Lists parity 3: permissions (ADR-0035), indexed fields, views, templates, smart folders, delta, bulk | done (steps below; smart folders moved to T12) |
| T09 | M | Extension SDK, extension host and generator | in progress: the SDK for ported modules first; term sets, template sections, shipped workflows and MCP tools come with T12, T13, T14 and T16 |
| T10 | S-M | Notes, Collaboration, Notifications | |
| T11 | M | Tasks and Calendar | |
| T12 | L | Taxonomy and Search (SQLite FTS5, optional semantic search), then smart folders (from T08g) | |
| T13 | M | Provisioning and templates | |
| T14 | L | Workflows parity (waits, approvals, schedules, `forEach`, `event.raise`, `steps`, built-ins) and AI workflows | |
| T15 | XL | Documents (PDF libraries, page images, OCR) | |
| T16 | M | MCP, admin CLI, backup and restore, Papermerge import, the audit log across modules | |
| T17 | XL | PostgreSQL build, SDK regeneration, web UI (with the authorization-code flow, passkeys, OAuth client applications and reverse-proxy sign-in) | |

Status values: empty = not started; "in progress"; "done" (with what moved to a later task).

### T08 steps (in order)

| Step | Task | Status |
|---|---|---|
| T08a | Item permissions: break/reset inheritance, grants for lists and items, folder contents follow scope changes (`ScopeMover`, `CompleteFolderScopeChange`) | done |
| T08b | Views (`/views`, `?viewId=` on item queries) | done |
| T08c | Item counts per field value (`/items/counts`; without indexed fields: JSON values, `json_each` for multi-value fields) | done |
| T08d | Bulk update as an operation (`/items/bulkUpdate`) | done |
| T08e | Delta (change log, `/items/delta`, scope changes for delta) | done |
| T08f | List templates and `templateKey` (`/listTemplates`; the document keywords field comes with T12) | done |
| T08g | Smart folders | moved to T12: they filter by and assign Taxonomy terms |
| T08h | Indexed fields (item columns and value table, backfill; no reflection over columns under AOT) | done |

### T09 steps (in order)

| Step | Task | Status |
|---|---|---|
| T09a | SDK core and extension host: manifest, catalog, per-tenant state and settings (tenant explicit), gated field types, item mutators, content types, list templates, recurring jobs, workflow activities, endpoints under `/v1.0/ext/{id}`, subscribers of list events; the build-time generator | done |
| T09b | Extension tables (`ExtensionDbContext` with a compiled model and precompiled queries, migrations as SQL per extension) and the Invoices sample (the parts whose modules are ported) | |
| later | Term sets (T12), template sections (T13), workflow triggers, shipped workflows and waits (T14), MCP tools (T16) | |

Done before this plan: building blocks, Identity (sign-in slice), Lists (slice), Audit (slice), Workflows (flow slice).
