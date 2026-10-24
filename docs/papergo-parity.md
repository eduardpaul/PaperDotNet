# PaperGo functional parity

PaperDotNet and PaperGo target the same product capabilities and similar
user-visible behavior. PaperDotNet keeps its .NET architecture and native APIs
([ADR-0047](adr/0047-papergo-functional-parity.md)). This register tracks the work;
it does **not** claim the two applications already have feature parity.

## Comparison baseline

- Reference: [eduardpaul/PaperGo, main at
  `485be496d5c3b9077c9a6238cd8f11d3e4bffeda`](https://github.com/eduardpaul/PaperGo/tree/485be496d5c3b9077c9a6238cd8f11d3e4bffeda), inspected 2026-10-23.
- PaperDotNet starting point: `96df412f040f53cc14711aaba35880e24817e084`.
- Evidence: both repositories' handlers, domain models, API descriptions,
  frontend routes and tests. PaperGo's older `reports/code-api-parity.md` and
  frontend backlog contain historical conclusions; they do not supersede its
  current code.
- The full PaperGo server and full Go test suite have not been run here. The
  pinned Go DAL was executed for the EF database round trip on both providers
  (see [database validation](papergo-database.md#verification-and-reference-upgrades)).

Functional parity means a user can accomplish the same job, with equivalent
visibility, permissions, defaults, lifecycle and failure behavior. It does not
require identical HTTP paths, JSON names, UUID/subject formats, cursor encoding,
environment variables or deployment internals. Cross-application package or
database compatibility is a separate contract. [ADR-0048](adr/0048-papergo-compatible-content-database.md) now pins a compatible EF organization database and lifecycle engine; existing HTTP/UI consumers still use module storage.

Statuses: **Implemented** identifies changes delivered by this alignment work;
**Mapped** means implementations exist in both and still require common
scenario verification; **Partial** identifies a narrower implementation or
observable difference; **Gap** means the PaperGo capability is missing in
PaperDotNet. An area marked Mapped is not certified interchangeable.

## Compatible database foundation

The compatible content engine maps all 64 pinned PaperGo tables through EF Core,
uses exact provider migrations and the reference fingerprint, and implements
schema/content revisions, optional history, draft/published projections,
publish/unpublish, metadata restore and archive/unarchive. It is available through
local administrator commands; the existing modular web API, UI and workers still
need integration. See [the database guide](papergo-database.md) for the exact
boundary, tests and interoperability probe. Do not apply module migrations to a
compatible organization database.

## Content lifecycle

| ID | Capability and PaperGo reference | PaperDotNet status and remaining work |
|---|---|---|
| LIFE-01 | Lists/libraries/folders/items, Home and group inboxes (`internal/dms`, `internal/httpapi/inboxes.go`) | **Mapped**: Lists, Workspaces and Documents modules; verify default content types, personal workspace discovery and group membership together. |
| LIFE-02 | History off by default for new lists/libraries; explicit opt-in (`backlog.md`, ISSUE-09; collection creation) | **Implemented** for metadata history through native creation, Documents templates, Home libraries and XML provisioning. Existing settings and explicit opt-in are retained. File-byte versions remain independent. Tests: `HistoryTests`, `DocumentTests`. |
| LIFE-03 | Explicit atomic populated-folder deletion (`docs/architecture.md`) | **Implemented**: `DELETE .../items/{id}?recursive=true`; ordinary deletion still rejects populated folders. Every descendant must be writable; a deletion hook veto aborts the complete selection. Tests: `FolderDeletionTests`, `PermissionTests`. |
| LIFE-04 | Recursive recovery of entries from the same folder-deletion operation (`internal/dms`, recycle bin) | **Partial**: the recycle bin restores individual entries, preserving identity/history; restore ancestors before descendants to retain hierarchy. Add one-operation subtree restore with permission checks and rollback. |
| LIFE-05 | Draft/published content surfaces for every list and library (`docs/architecture.md`) | **Partial**: compatible EF engine and local commands implement independent surfaces and permission actions (ADR-0048). Existing module HTTP queries, search, files, relationships, permissions and UI still need integration with that engine. |
| LIFE-06 | Immutable effective schema paired with every content revision (`docs/application-foundation.md`) | **Partial**: compatible EF engine freezes fields, content types and rules with each revision. Existing module history lacks those snapshots; migrate its writers/readers before certifying web behavior. |
| LIFE-07 | Archive/unarchive without creating a content revision (`docs/archiving.md`) | **Partial**: compatible EF engine implements version checks, idempotency, preserved content stamps and atomic audit/events. Existing HTTP/UI modules, packages and delta consumers still need integration. |
| LIFE-08 | Global Archive, protected Archived collection view and active/archived/all selection (`docs/archiving.md`) | **Partial**: EF collection browsing applies active/archived/all selection before pagination. Global Archive, protected web views, query totals/facets and search integration remain gaps. |
| LIFE-09 | Atomic selected-item/folder moves and rename/replace conflict resolution (`internal/httpapi`, `backlog.md`, ISSUE-05) | **Partial**: identity-preserving item moves and intra-list folder moves exist. Add atomic mixed selections, nested-selection rejection and collision handling; preserve relationships, files, ACL behavior and history. |
| LIFE-10 | Atomic explicit bulk actions alongside filtered partial-success jobs (`docs/application-foundation.md`) | **Partial**: filtered bulk updates and independent HTTP batch calls exist. Add a separate all-or-nothing explicit selection operation; do not change HTTP batch's independent-result semantics. |

## Schema, browsing and access

| ID | Capability and PaperGo reference | PaperDotNet status and remaining work |
|---|---|---|
| DATA-01 | Rich typed fields, defaults and live shared content types (`docs/application-foundation.md`) | **Mapped** for core field types and shared content types. Audit integer/fixed-scale decimal fields, precision, null/multi-value behavior, stable field identities and schema edit rules separately. |
| DATA-02 | Organization field catalog, cross-field validation and transactional business keys (`docs/application-foundation.md`) | **Partial**: fields are embedded in content types and core type validation exists. Add independent reusable field definitions, cross-field rules and unique-value reservations, including bulk/workflow writes and published values. |
| DATA-03 | Intrinsic keywords on all items and folders (`docs/application-foundation.md`) | **Partial**: keyword fields and the Documents content type exist. Make keyword capabilities consistent for arbitrary list content and folders, including discovery, counts and schema editors. |
| DATA-04 | Saved views, table/board/calendar/gallery, typed filtering, grouping and paging (`docs/application-foundation.md`, `web/src`) | **Mapped**: native OData queries, views, counts and layouts exist. Verify missing-value sorting, multi-sort continuations, folder selection, selected columns and authorization before paging/counting. |
| DATA-05 | Smart-folder visual rules, previews, classification and portable definitions (`docs/smart-folders.md`) | **Mapped** for smart folders and classification. Audit typed rule editing, previews, package flows and new archive/content-surface choices. |
| DATA-06 | Controlled taxonomy, CSV/package imports, moves/merges and keyword promotion (`internal/httpapi/taxonomy.go`, `web/src`) | **Mapped**: term-store APIs and administration exist. Verify multilingual labels, deprecation, recursive branches and merge effects on queries, links and stored values. |
| DATA-07 | Typed relationship attributes, endpoint limits and graph browsing (`docs/application-foundation.md`) | **Partial**: directed/symmetric taxonomy-backed relationships, endpoint limits, attributes and workspace graph queries exist. Compare typed attribute schemas, invalid attributes, edge ETags and endpoint visibility. Preserve .NET's cross-workspace link capability. |
| IAM-01 | Local accounts, OIDC/proxy sign-in, groups, nested groups, roles and tokens (`docs/identity.md`) | **Mapped** for local/proxy access, directory/roles and scoped personal tokens. Audit external-provider browser sign-in, first-admin enrollment, service credentials and identity synchronization. Keep .NET's OAuth server and passkeys. |
| IAM-02 | Independent read/read-draft/write/publish/manage resource actions (`docs/architecture.md`) | **Partial**: inherited scope ACLs use ordered Read/Contribute/Manage levels. Add draft and publish actions with publication support; every module scope remains independently enforced. |

## Documents, search and AI

| ID | Capability and PaperGo reference | PaperDotNet status and remaining work |
|---|---|---|
| DOC-01 | Signature-checked PDF/TIFF/JPEG/PNG/WebP intake, replacement, byte history and restoration (`docs/application-foundation.md`) | **Mapped**: Documents supports these formats and file versions. Verify streamed size limits, truncated signatures, filename normalization, metadata-only versus byte restoration and content-package intake. |
| DOC-02 | Duplicate policy across all currently visible documents (`README.md`, upload handlers) | **Partial**: allow/warn/block exists; audit library/workspace search boundaries and permissions before claiming equivalent detection. Same bytes must not reveal an inaccessible item's existence. |
| DOC-03 | Asynchronous bounded WebP previews with dedicated worker and explicit pending/unavailable states (`docs/previews.md`) | **Partial**: workflow-generated thumbnails/page images exist, with different rendition and status contracts. Add equivalent size/pixel limits, source-pinned caching, independent scheduling and retry semantics. |
| DOC-04 | Local PaddleOCR recognition with bundled models (`docs/ocr.md`) | **Partial**: Tesseract and optional GLM perform recognition; the PaddleOCR detector is used by storage optimization. Full local recognition, model readiness and languages need their own .NET implementation and packaging. |
| DOC-05 | Stored text/artifacts, reviewed transformations, staged promotion, composition and reclaim (`docs/documents.md`) | **Partial**: page tools and reviewed storage optimization exist. Compare generic artifact ownership, source-version checks, task review, safe promotion and orphan cleanup. |
| DOC-06 | Per-library WebDAV browse/read/create/overwrite/rename/move/copy/lock/delete (`README.md#webdav`, `internal/webdav`) | **Gap**: implement on Documents/Lists contracts, with the same authorization, ETags, publishing, filename uniqueness, history and explicit folder deletion behavior. |
| SRC-01 | Keyword/full-text/semantic/hybrid search with ACL trimming, facets and snippets (`docs/search.md`) | **Mapped**: database and optional zvec stores and semantic/hybrid services exist. Verify folder inclusion, query syntax, field filters, counts/facets, reindex status and backend consistency. |
| AI-01 | Bundled local embeddings and deterministic local decisions (`docs/embeddings.md`, `docs/decisions.md`) | **Gap** for bundled local inference and decision/taxonomy-decision APIs. Add opt-in .NET providers, model validation, lazy loading, limits, cancellation and budget/cache ownership; keep hosted providers. |
| AI-02 | Provider chat completions, batch processing and structured outputs (`docs/ai.md`) | **Mapped**: AiWorkflows has extract/classify/summarize/prompt, cache/budgets and provider batches. Verify reusable operation-level APIs and restarts, ownership and source-version checks. |

## Automation and operations

| ID | Capability and PaperGo reference | PaperDotNet status and remaining work |
|---|---|---|
| WF-01 | Complete TypeScript workflows with durable API-call replay (`docs/workflows.md`) | **Partial**: native graph definitions, Jint script nodes and resumable planned writes exist. Full typed script authoring, awaited API calls, recorded nondeterminism and immutable replay journals are different capabilities; preserve the existing graph engine while supplying equivalent scenarios. |
| WF-02 | Distinct item/file/folder events, manual selection cardinality, author authority and frozen trigger snapshots (`docs/workflows.md`) | **Partial**: immutable event values, selection launches, schedules and module events exist. Audit file-only versus metadata events, selected-item snapshots and run authority; never bypass the user's access to emulate a system workflow. |
| WF-03 | Workflow check/explain/test runs, designer/code editing, parameters, versions and typed forms (`internal/httpapi/workflows.go`, `web/src`) | **Partial**: workflow editor, activity catalog, versions and schema-driven launch forms exist. Add isolated test runs, preflight diagnostics/explanation and equivalent authoring tools. |
| WF-04 | Durable human tasks with review packages, reassignment and guarded completion (`internal/httpapi/tasks.go`) | **Partial**: approvals and user tasks exist. Compare generic task forms, attached/staged artifact review, ownership rechecks, reassignment and cancellation under workflow replay. |
| OPS-01 | One durable operation lifecycle with claims, parking, cancellation, retention and live updates (`docs/operations.md`) | **Mapped** for jobs/operations, durable workflows and SSE. Compare ownership on every read/retry/cancel, parked work, lease recovery, retention and visible progress. |
| OPS-02 | Storage diagnostics, allocation/WAL progress, retention candidates and operator UI (`README.md#deployment-and-scaling`) | **Gap** for equivalent storage diagnostics. Keep provider-specific measurements behind SQLite/PostgreSQL implementations; viewing status cannot delete data. |
| OPS-03 | Workspace/configuration/content packages, validation and admin import/export (`docs/workspace-portability.md`) | **Mapped**: XML/configuration/content packages and operation-based portability exist. Audit all new lifecycle/artifact records, reference remapping, file checksums and invalid-package rollback. Native formats remain supported. |
| OPS-04 | Online backup, stopped restore, native releases and one-container operation (`docs/releases.md`, `docs/development.md`) | **Partial**: native development, Docker, backup/restore and SQLite/PostgreSQL exist. Add equivalent standalone distribution/assets, checksum/model/native-library smoke tests and resource diagnostics without requiring Docker at runtime. |
| MCP-01 | Scoped assistant connections and client-specific tool exposure (`docs/mcp.md`) | **Partial**: authenticated MCP tools and extension contracts exist. Compare connection credential lifetimes, tool visibility and assistant-specific scope caps. |

## Capabilities to preserve and align in the other direction

PaperDotNet already has product areas beyond PaperGo's original DMS foundation:
tenant lifecycle, built-in OAuth/passkeys, recurring personal tasks, calendar
and feeds, notes/wiki links, comments/mentions, notifications/digests/webhooks,
and per-tenant extensions. Keep their existing APIs, screens and tests. Audit
PaperGo's current module implementation before declaring any area missing there;
its older parity report is not a current certification. PaperGo changes are
outside this repository's implementation work.

## Delivery order and completion gates

1. **Lifecycle foundation:** LIFE-02/03 regression tests, LIFE-04 subtree
   recovery, LIFE-09/10 atomic selections. Settle draft/publish permissions and
   snapshot contracts for LIFE-05/06 and IAM-02 before changing read behavior.
2. **Published content and archive:** LIFE-05/06/07/08 across database queries,
   search, relationship/lookup discovery, file downloads/previews, history,
   delta, packages and UI. Include invisible drafts, unpublished replacements,
   stale index entries and tenant isolation in the test matrix.
3. **Schemas and file access:** DATA-01/02/03/07, DOC-02/06. WebDAV must consume
   the same content and permission primitives as REST.
4. **Document processing and local AI:** DOC-03/04/05, AI-01/02 and SRC-01.
   Verify real native models and source-version recovery, not placeholder
   operation results.
5. **Workflow parity:** WF-01/02/03/04 on the established operation/artifact
   contracts. Cover restart/replay, nondeterminism, side effects, permissions,
   selection cardinality and human completion.
6. **Operations and release parity:** OPS-01/02/03/04, IAM-01 and MCP-01;
   package/container smoke tests and operational recovery on both providers.

For every completed requirement record source evidence and tests, including a
member's UI path where appropriate. Run matching scenarios against the pinned
PaperGo reference before upgrading a source-mapped area to verified parity.
Test SQLite and PostgreSQL for persistence/authorization changes; do not mark
PostgreSQL verified from a SQLite-only run. New reference commits require a
fresh review of the affected scenarios, rather than silently changing this
baseline.

## Compatible database validation (2026-10-23)

- All 16 content/schema tests passed on SQLite and PostgreSQL.
- SQLite matched all 240 reference schema objects and both providers retained
  the exact reference fingerprint, seed data and all 64 EF table mappings.
- The actual pinned Go DAL read EF content, archived it and recorded another
  schema; EF then read the Go archive, edited under the Go schema and
  unarchived the item. The round trip passed on both providers.
- This validates the compatible engine/commands; existing modular web consumers
  still need integration. See ADR-0048 and the operator guide for the boundary.

## Verification commands

Validation of the initial lifecycle changes (2026-10-23):

- Full solution build: zero warnings and errors.
- SQLite integration, architecture and sample extension projects passed.
  The full run discovered 723 cases (24 optional-provider/native-library
  skips). One existing live-event timing unit test failed under concurrent
  build/test load; the entire unit project then passed independently, 270/270.
- PostgreSQL: all 20 focused folder deletion, permission and history tests
  passed using an ordinary application role with row-level security.
- Web: typecheck, lint, formatting and all 44 unit tests passed; production
  build passed.
- Browser: seven existing list scenarios passed; the new folder-confirmation
  and recursive-recycle scenario passed after correcting the test to wait for
  deletion completion before navigating away.
- C#, TypeScript and Python request builders regenerated from OpenAPI;
  changed C# source passed whitespace verification.

These results verify the initial .NET changes. They do not certify the remaining
requirements or cross-implementation behavior against a running PaperGo server.

```bash
dotnet build PaperDotNet.slnx
dotnet test --solution PaperDotNet.slnx
PAPERDOTNET_TEST_PROVIDER=postgresql dotnet test --solution PaperDotNet.slnx
npm run check -w web
npm run test:e2e -w web
```

The PostgreSQL command requires Docker/Testcontainers or the documented
`PAPERDOTNET_TEST_POSTGRES` connection. Regenerate SDKs after API changes as
described in [sdk/README.md](../sdk/README.md).
