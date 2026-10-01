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
| T09 | M | Extension SDK, extension host and generator | done (steps below): term sets, template sections, workflow triggers and shipped workflows, MCP tools come with T12, T13, T14 and T16 |
| T10 | S-M | Notes, Collaboration, Notifications | done (steps below); the message store keeps SQLite's own cache and no mmap (ADR-0039) |
| T11 | M | Tasks and Calendar | done (steps below); Ical.Net works under AOT with its assembly rooted |
| T12 | L | Taxonomy and Search (SQLite FTS5, optional semantic search), then smart folders (from T08g), note `#tags` and comments in search (from T10) | done (steps below; semantic search is T12e, after the AI workflows of T14). AOT smoke with smart folders: idle 138 MB (close to the 150 MB budget: watch it), 226 MB under load |
| T13 | M | Provisioning and templates | in progress (steps below) |
| T14 | L | Workflows parity (waits, approvals, schedules, `forEach`, `event.raise`, `steps`, built-ins, the `comment.added` trigger) and AI workflows | |
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
| T09b | Extension tables (`ExtensionDbContext` with a compiled model and precompiled queries, migrations as SQL per extension) and the Invoices sample (the parts whose modules are ported), `Extensions.Testing` | done |
| later | Term sets (done in T12b), template sections (T13), workflow triggers, shipped workflows and waits (T14), MCP tools (T16) | |

### T10 steps (in order)

| Step | Task | Status |
|---|---|---|
| T10a | Notifications: inbox, settings, webhooks (signed, retried, quiet hours), follows with alerts and digests, the `notify` activity (with `IWorkflowRecipients`) | done. Posts name the tenant by id (`tenantId`); reminders come with T11 |
| T10b | Change subscriptions (API-06): validation handshake, signed deliveries, cleanup | done |
| T10c | Collaboration: comments with mentions, the activity timeline (`IItemActivity`); comments in search come with T12, the `comment.added` trigger with T14 | done. `IItemActivity.RecordAsync` takes the actor (tenant explicit) |
| T10d | Notes: content type and template, wiki links and backlinks, link updates on renames; `#tags` as keywords come with T12 | done |

### T11 steps (in order)

| Step | Task | Status |
|---|---|---|
| T11a | Tasks: content type and template, checklists, subtasks and dependencies, recurrence (Ical.Net, rooted for the trimmer), my tasks, tasks from documents, `task.create`, due reminders; the `task.completed` trigger comes with T14 | done |
| T11b | Calendar: content type and template, event times, series with exceptions in their time zone, time ranges, iCalendar export/import, feeds, event reminders | done. Occurrence starts are stored as Unix milliseconds |

### T12 steps (in order)

| Step | Task | Status |
|---|---|---|
| T12a | Taxonomy: term groups, sets and hierarchical terms (labels, synonyms, colors), keywords, merges (`TermMerged`), promotion, SharePoint CSV import, `ITermStore` and `ITermSetProvisioning` with the tenant named | done. Popular keywords come with T12c (they count tagged items); the template section with T13 |
| T12b | Lists: `managedMetadata` and `keywords` fields (term resolution, hierarchical filters), the `TermMerged` subscriber, note `#tags`, extension term sets (`AddTermSet`) | done. Filters on a term match its subtree (looked up before the SQL is built); merges rewrite values through the item store, so versions, events and the delta follow; the documents content type has its keywords field again |
| T12c | Search: SQLite FTS5 index from item events, access trimming by scope, page hits, comments, `ITermUsage` and popular keywords | done. FTS5 tables keyed on declared integer keys (row ids survive `VACUUM`), SQL per provider (`ISearchQueries`), id sets as one `json_each` parameter; permission moves refresh document scopes after the folder move completes |
| T12d | Smart folders (from T08g): saved term filters that assign terms | done. Each list runs the folder's OData filter (term subtrees through `TermHierarchy`), pages merge newest first with a keyset cursor on `updatedAt`/`id`; sub-folders count values per list. Shared folders in workspace templates came with T13b |
| T12e | Semantic and hybrid search (embeddings, `AI:Embeddings`), after T14's AI workflows; the search MCP tool with T16 | |

### T13 steps (in order)

| Step | Task | Status |
|---|---|---|
| T13a | Provisioning engine and contracts with the tenant and actor on `TemplateContext`, XML apply/export/schema endpoints, the `Workspace` and `List` containers, the `ContentTypes` section | done |
| T13b | Sections: groups, roles and users (Identity), term groups (Taxonomy), smart folders (Lists), workflows, extensions (enable, settings, `IExtensionBuilder.AddTemplateSection`) | done. Workflow sections carry custom workflows; built-in workflows in templates come with T14, the documents section with T15 |
| T13c | Packages with content (zip: items, folders, values, files), export and import as operations, cleanup job | |

Done before this plan: building blocks, Identity (sign-in slice), Lists (slice), Audit (slice), Workflows (flow slice).
