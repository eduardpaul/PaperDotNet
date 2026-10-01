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
| T13 | M | Provisioning and templates | done (steps below); built-in workflows in templates come with T14, documents in packages with T15. AOT smoke with packages, exports and an approval (T14a): idle 132 MB, 254 MB under load |
| T14 | L | Workflows parity (waits, approvals, schedules, `forEach`, `event.raise`, `steps`, built-ins, the `comment.added` trigger) and AI workflows | done (steps below). AOT smoke with the AI module and the OpenAI SDK: binary 87 MB, idle 140 MB, 233 MB under load |
| T15 | XL | Documents (PDF libraries, page images, OCR) | done (steps below). AOT smoke with a PDF upload (text, thumbnail, page image, search): binary 98 MB, idle 135 MB after a post-start GC compaction, 281 MB under load with the workstation GC |
| T16 | M | MCP, admin CLI, backup and restore, Papermerge import, the audit log across modules | done (steps below); the Papermerge import tool and the client CLI moved to T17 |
| T17 | XL | PostgreSQL build, SDK regeneration, web UI (with the authorization-code flow, passkeys, OAuth client applications and reverse-proxy sign-in); the Papermerge import tool and the client CLI `pdn`; what is left in `ToPort/` | in progress (steps below) |

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
| T13c | Packages with content (zip: items, folders, values, files), export and import as operations, cleanup job | done. The `Items` section (portable values, folders, stamps through `AuditOverrides`, item permissions) on `/provisioning/export?includeContent=true` and `/provisioning/apply`; `/v1.0/portability` exports and imports as operations with their own table and a daily cleanup. Library files and versions in packages come with T15 (documents section, `ToPort/PackageHistoryTests.cs`), the CLI export and import with T16 |

### T14 steps (in order)

| Step | Task | Status |
|---|---|---|
| T14a | Waits: bookmarks (`WorkflowActivityResult.Wait`/`WaitAndRunAgain`, `IWorkflowBookmarks`), time-outs and the `delay` activity, approvals (`approval` with approvers, escalation and outcome ports) | done. Bookmarks and approvals have their own tables (times as Unix ms); `IWorkflowBookmarks` names the tenant; the minute job (`workflows.timers`) ends due waits and escalates; runs can be cancelled and retried; `/v1.0/me/approvals`. The Invoices sample's `awaitPayment` works again. Retry policies of nodes come with T14b, `approval.decided` with T14c |
| T14b | Flow parity: the `steps` form, `forEach`, retries, `event.raise` and `wf.{key}.completed/failed` triggers, restored-item triggers, run cleanup, concurrency per item | done. Steps compile to a flow; workflows get a stable `key` (from the name, kept on rename); workflow events are `WorkflowTriggerRaised` integration events (Workflows.Contracts) with ids made from the run, so a repeat starts nothing twice; retries wait on `retry` bookmarks (no delay: resumed at once); the daily `workflows.runCleanup` job removes old runs in batches |
| T14c | Triggers: schedules (cron), date fields, several triggers, module triggers through `IWorkflowTriggers` (`task.completed`, `comment.added`), extension triggers (`AddWorkflowTrigger`), term filters, manual inputs and selections | done. `IWorkflowTriggers` names the tenant (an actor or the causing event); `approval.decided` is raised with decisions; triggers narrow by list, content type, changed fields, terms (with terms below) and data; `/v1.0/workflows/triggers` lists them; the minute job `workflows.schedules` keeps state per timed trigger (Unix ms); the Invoices sample's `approvalNeeded` trigger works again |
| T14d | Built-in and shipped workflows (`AddWorkflow`, `Scope = Library`), extension workflows, `BuiltIn` elements in templates | done. `IWorkflowDefinitionProvider` with the tenant (an extension's are offered only where it is enabled); `…/workflows/builtIns` (catalog, turn on with parameters and `If-Match`, copy) and per library; built-in rows are read-only; hourly `workflows.builtIns` sync; templates carry the key and parameters (set after the lists of the run) and copies' `CopiedFrom`; the Invoices sample's `approveAndCollect` works again. AI requirements come with T14e, per-library defaults with documents (T15). AOT smoke after T14b–d: idle 145 MB (at the edge of the 150 MB budget: watch it), 225 MB under load |
| T14e | AI workflows (Microsoft.Extensions.AI, off by default): extract, classify, summarize, prompt, `ai.batch` | done. AiWorkflows module (calls, cache, daily budget, cleanup job), `ai.*` activities resolve their scoped services from the run, batched AI on waits (`IWorkflowBookmarks`, `IWorkflowDirectory`), the "AI batch" built-in requires a chat model (`IWorkflowRequirement`); `PaperDotNet.AI` (OpenAI SDK, `AI:Chat`, `AI:Batch`, `AI:Embeddings`) is in the AOT build; activities describe `InputSchema`/`OutputSchema` in the catalog. Page images (`IItemPageImageSource`) come with Documents (T15) |

### T15 steps (in order)

| Step | Task | Status |
|---|---|---|
| T15a | Documents core: own DbContext (stored files deduplicated by SHA-256, file versions, library settings, page text), upload, download and versions endpoints, duplicate policy, `document.added`, purge cleanup and the stored-file cleanup job, file text in search (`IItemSearchContributor`) | done. Multipart uploads bound by the request delegate generator (`IFormFile` needs source-generated JSON metadata for OpenAPI); duplicate policies are strings (`allow`, `warn`, `block`); `document.added` is a module trigger raised with the uploader as actor |
| T15b | Processing as built-in library workflows: text (PdfPig), thumbnails and page images (PDFtoImage: PDFium + SkiaSharp, native libraries next to the binary), OCR (Tesseract CLI; GLM-OCR over HTTP), `IItemPageImageSource` for AI, per-library defaults of built-ins; AOT smoke with a PDF upload | done. Activities are singletons that resolve the module's scoped services from the run; OCR is an operation that completes the step's wait; GLM-OCR JSON is source-generated; `…/workflows/runs?itemId=` lists an item's runs; library defaults are created one at a time per process and item indexing is serialized per item (no stale overwrite of a file's text); the image installs Tesseract (eng, deu) and copies the native libraries |
| T15c | Page operations (PDFsharp: rotate, delete, reorder, split, merge), library settings and document files in templates and packages, the Home libraries (`me/inbox`) and group inboxes; MCP document tools come with T16 | done. Lists has `/v1.0/me/home` and `IListItemStore.EnsureHomeAsync` again (system lists unique per workspace); page editors and inboxes take the caller's actor; packages carry every file version with page texts and stamps; an import that finishes before its id is noted no longer fails the request (the old Portability flake) |

### T16 steps (in order)

| Step | Task | Status |
|---|---|---|
| T16a | MCP server (`/v1.0/mcp`, ModelContextProtocol SDK): tool contract with JSON-node results (no reflection), list and item tools, document tools, the search tool (from T12e), extension tools (`IExtensionBuilder.AddMcpTool`) | done. Tools build results with `JsonObject` (`McpToolResult.FromJson`), take the tenant from `Caller`, and the scope check runs the scope requirements directly; extension tools are gated per tenant (`tests_tickets_count`, sample `samples_invoices_pending`); the search tool is keyword-only until T12e |
| T16b | The audit log across modules (`/v1.0/auditLog` by entity type), from T07 | done. The save guard records changes of tenant-owned entities (not `INotAudited` ones: runs, deliveries, search rows, item versions and values, page text) and writes them through `IAuditLogWriter` (plain SQL on the saving context's connection, `SqliteAuditLogWriter`) in the transaction of the change, beginning one when the save has none; `/v1.0/auditLog` filters by entity type, entity, user and time in one precompiled query (absent filters are null parameters). The event log `/v1.0/audit` stays |
| T16c | Admin commands of the server binary (`paperdotnet migrate`, `bootstrap`, `tenant`, `user`, `backup`, `restore`, `reindex`, `export`, `import`), backup and restore (from T13: the CLI export and import of packages) | done. System.CommandLine in the AOT host; commands run on the built application without serving and migrate first; backups are a `.tar.gz` with a manifest, a SQLite snapshot (online backup API, `IDatabaseBackup`) and the stored files. The Papermerge import tool (`PaperDotNet.Import.Papermerge`, a package writer on PostgreSQL) and the client CLI `pdn` (on the C# SDK) move to T17, with the PostgreSQL build and the regenerated SDK they need |
| T16d | Tenants from the host name (custom hosts, host template) and the `X-Tenant` header (the old Tenancy module on Finbuckle), behind a reverse proxy; then remove `src/Modules/Tenancy` | done. `TenantResolver` in Identity (custom hosts in `tenant_hosts`, `Tenancy:HostTemplate` with `{tenant}`, `Tenancy:AllowHeader`), a short cache of found tenants; the token endpoint signs in to the named tenant, `TenantGuardMiddleware` answers `404 tenantNotFound` and `403 tenantMismatch`; `/v1.0/organization` lists the hosts; `paperdotnet tenant create --host`. Finbuckle and `PaperDotNet.Tenancy` are gone (the contracts stay) |

Done before this plan: building blocks, Identity (sign-in slice), Lists (slice), Audit (slice), Workflows (flow slice).

### T17 steps (in order)

| Step | Task | Status |
|---|---|---|
| T17a | Sweep `ToPort/`: port the cases still missing for ported modules (lists, workflows and scripts, permissions and scope fan-out, extensions and extension tables, workspaces, nested groups, indexed fields, events and jobs, cross-list queries, portability, the receipts package, health and OpenAPI document, tenant isolation) and delete the files they cover | |
| T17b | JSON batching (`/v1.0/$batch`, `ToPort/BatchTests.cs`) | done. Sub-requests run through a branch of the application's pipeline (routing, authentication, the tenant guard, authorization); bodies through source-generated JSON (`HostJson`). Endpoints mapped by the host itself are not in the OpenAPI document yet: T17e |
| T17c | Semantic and hybrid search (T12e: embeddings when `AI:Embeddings` is configured, `ToPort/SemanticSearchTests.cs`) | |
| T17d | Sign-in for apps: `/connect/authorize` (authorization code with PKCE), OAuth client applications, passkeys, account pages, reverse-proxy sign-in (`ToPort/OAuthTests.cs`, `AuthenticationTests.cs`, `AccountTests.cs`, `ReverseProxyTests.cs`) | |
| T17e | SDK regeneration from the AOT API (`sdk/openapi.json`, Kiota C#, TypeScript and Python; `SdkContractTests`, `ClientSdkTests`; `$batch` in the document) and the client CLI `pdn` (`ClientCliTests`) | |
| T17f | Web UI on the AOT API (`web/`, Playwright, `WebUiTests`; the CI job back on) | |
| T17g | PostgreSQL build (provider, migrations as SQL, RLS, LISTEN/NOTIFY for live events, `LiveEventBackplaneTests`) and the Papermerge import tool (`PapermergeImportTests`) | |
