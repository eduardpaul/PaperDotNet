# PaperDotNet: architecture vision

**Status:** draft proposal for discussion
**Inputs:**
- Papermerge, a document management system (see [papermerge-features.md](papermerge-features.md))
- SharePoint lists and libraries

> **Current scope (decided 2026-09-24): backend API only.** Work focuses on the
> ASP.NET Core API, workers, data model, extension runtime (server side), SDK
> generation and MCP. The web UI, the frontend SDK, UI extension points and the
> mobile app are **deferred**. The UI-related parts of this document stay as
> the long-term direction, but no UI work is planned yet. The API must still
> provide everything a future UI needs (e.g. real-time events, thumbnails,
> view definitions).

> **Guiding principle (decided 2026-09-24): self-hosting and practicality come first.**
> Simplicity here means **practicality**: whatever is easiest to build,
> run and maintain. It does not mean the fewest dependencies.
> - The minimal install is **one container** (SQLite database on a volume,
>   ADR-0009). `docker compose up` must give a fully working system.
>   PostgreSQL is an option for larger installations.
> - That includes OCR, search, background jobs, auth (local accounts + API
>   tokens) and the MCP server.
> - Everything else is **optional and off by default**: external IdP, S3
>   storage, a Redis-protocol cache, external search/vector engines, separate
>   worker containers, AI providers. None of them may be required to run.
> - **Extra services cost self-hosters**, so keep them optional.
>   **In-process libraries are fine** when they save us writing and
>   maintaining code. Use a mature library rather than reinventing it
>   (scheduling, OAuth, query parsing, PDF handling).
> - **Never hand-roll** security, protocol or file-format code.
> - Write our own code only where it is small, core to the product, or where
>   no good library exists.

## 1. Goal

PaperDotNet is a platform for **structured content**. It ships with three built-in apps:

- **Documents**: a document management system (DMS) modelled on Papermerge.
- **Tasks**
- **Calendar**

Any other kind of data can be added without changing the core, such as contacts, assets, invoices, tickets or projects. Developers can extend almost every part of the system through **extensions**.

Guiding rule: **the core knows nothing about documents, tasks or events.** It knows about lists, items, fields, files, permissions and events. Documents, Tasks and Calendar are the first extensions built on that core. If they can be built as extensions, anything can.

## 2. Core concepts (SharePoint-style model)

| Concept | Meaning | SharePoint | Papermerge |
|---|---|---|---|
| **Workspace** | Top-level container with members, permissions, navigation and installed extensions | Site | — (per-user Home) |
| **List** | A collection of items that share a schema | List | — |
| **Library** | A list whose items carry files (versions, preview, OCR) | Document library | Document tree |
| **Folder** | Optional hierarchy inside a list or library | Folder | Folder node |
| **Content type** | A named, reusable schema: an ordered set of fields plus behaviour. A list can hold several | Content type | Document type |
| **Field (column)** | A typed attribute: text, number, date, choice, person, lookup, tags… | Site column | Custom field |
| **Item** | One record: field values, metadata, version history, ACL | List item | Node / document |
| **View** | A saved way to show a list: columns, filter, sort, group, layout (table, board, calendar, gallery, timeline) | View | Commander / table view |
| **Taxonomy** | Shared term sets: tags and hierarchical categories | Managed metadata | Tags |

The built-in apps map onto this model as follows:

- **Documents**: a library with content types such as *Document*, *Invoice* or *Contract*. Page management, OCR, thumbnails and text extraction attach to library files.
- **Tasks**: a list with a *Task* content type. Fields: title, status, priority, assignees (person), due date, start date, parent task (lookup), checklist, related items (lookup to any list, for example "task about this contract"). Views: table, board, my tasks, timeline.
- **Calendar**: a list with an *Event* content type. Fields: start, end, all-day, recurrence (RRULE), location, attendees, reminders. Views: month, week, day, agenda. Tasks with due dates can also appear on the calendar.

The **lookup / relation field** is what turns this from three separate apps into one productivity suite. Any item can link to any other item, for example:

- document ↔ task
- event ↔ document
- task ↔ task

## 3. Extensibility model

### 3.1 Extension package

An extension is a versioned package with a manifest that declares everything it contributes. This follows the VS Code approach.

```jsonc
{
  "id": "acme.invoices",
  "version": "1.2.0",
  "requires": { "paperdotnet": ">=1.0", "extensions": { "core.documents": "^1.0" } },
  "permissions": ["items.read", "items.write", "files.read", "http:api.acme.com"],
  "contributes": {
    "fieldTypes":    ["Iban", "Currency"],
    "contentTypes":  ["Invoice"],
    "listTemplates": ["Invoices"],
    "views":         ["InvoiceAgingBoard"],
    "commands":      [{ "id": "invoices.pay", "when": "item.contentType == 'Invoice'" }],
    "eventHandlers": ["item.created:Invoice"],
    "fileProcessors":["application/pdf"],
    "settings":      "settings.schema.json",
    "ui":            { "entry": "ui/index.js" }
  }
}
```

### 3.2 Extension points

| # | Extension point | What an extension can do | Built-in examples |
|---|---|---|---|
| 1 | **Field types** | New column types. Each has server validation, storage mapping, search/filter operators, and UI editor, display and filter components | Text, Number, Money, Date, Choice, Person, Lookup, Tags, Location, Rating |
| 2 | **Content types / list templates** | Ship ready-made schemas plus default views, commands and settings | Document, Task, Event, Contact |
| 3 | **Views / layouts** | New ways to display a list | Table, Board (kanban), Calendar, Gallery, Timeline/Gantt, Map |
| 4 | **Commands** | Toolbar, context-menu and bulk actions, with `when` conditions | Rotate pages, Mark done, Export .ics |
| 5 | **Item event handlers** | *Before* handlers (sync; can validate, modify or cancel) and *after* handlers (async), for item, file and permission events. These work like SharePoint event receivers | Recompute path template, send notification |
| 6 | **File processing pipeline** | Ordered processors per MIME type: text extraction, OCR, thumbnails, classification, virus scan | Papermerge OCR, PDF preview, Office preview |
| 7 | **Automation triggers and actions** | Nodes for a no-code rules/workflow engine: "when X, if Y, do Z" | Papermerge "automates": auto-tag, auto-file |
| 8 | **Background jobs** | Scheduled and queued work | Reminders, recurring-task generation, reindexing |
| 9 | **API endpoints** | Custom REST endpoints under `/api/ext/{id}/…` | Invoice payment callback |
| 10 | **Search** | Custom indexers, facets and result renderers | OCR text, calendar date facet |
| 11 | **UI contributions** | Pages, navigation entries, item detail panels/tabs, dashboard widgets, settings pages | Document viewer, "My day" widget |
| 12 | **Providers** | Pluggable implementations of core services | Storage (local, S3), Auth (OIDC, LDAP), Notifications (email, push), Search engine |
| 13 | **Import / export & sync** | Connectors to external systems | iCal/CalDAV, IMAP to library, CSV/Excel |
| 14 | **Permissions** | Extensions declare their own permission scopes, which appear in the role editor | `invoices.approve` |

### 3.3 How extensions run

There are two tiers, which use the same manifest and contracts:

1. **In-process extensions (trusted, .NET).** NuGet-style packages loaded with `AssemblyLoadContext`. They register services through an `IExtension.Configure(IExtensionBuilder)` entry point. This is the fastest and most powerful tier. It is meant for first-party and admin-approved extensions.
2. **Remote extensions (sandboxed, any language).** These run out of process and talk to the core over HTTP:
   - They receive signed webhooks for events.
   - They call back into the REST API with a scoped token.
   - They serve UI in a sandboxed iframe, or as a web component through a host SDK.

   This tier is safe for third-party or marketplace code.

The **frontend** is a host shell that loads extension UI bundles dynamically (ES modules or module federation) through a typed `@paperdotnet/sdk`. The SDK offers registries for field editors, views, commands, panels, widgets and routes.

**Rule:** first-party features (Documents, Tasks, Calendar) are built only against the public extension SDK. This keeps the SDK complete.

## 4. Proposed technical architecture

```
┌──────────────────────── Web UI (host shell + extension bundles) ───────────────────────┐
│  React + TypeScript SPA · @paperdotnet/sdk registries · pdf.js viewer · SignalR client │
└───────────────────────────────────────────┬────────────────────────────────────────────┘
                                             │ REST (OpenAPI) + SignalR
┌───────────────────────────────── ASP.NET Core API host ─────────────────────────────────┐
│  Core modules: Workspaces · Lists/Schema · Items · Files/Versions · Views · Taxonomy   │
│                Security (ACL + roles/scopes) · Search · Events · Automation · Audit    │
│  Extension runtime: manifest loader · AssemblyLoadContext · webhook dispatcher         │
│  Built-in extensions: Documents (DMS) · Tasks · Calendar                               │
└───────┬──────────────────────┬─────────────────────────┬────────────────────────────────┘
        │                      │                         │
   PostgreSQL             Object storage           Job queue / workers
   (EF Core, JSONB,       (local / S3 / R2)        (outbox → workers: OCR, thumbnails,
    tsvector FTS)                                   indexing, reminders, webhooks)
```

The detailed, current technical decisions are in
**[technical-approach.md](technical-approach.md)**. In short:

- **Modular monolith** on .NET 10 (one container) with vertical slices, and module boundaries enforced by tests.
- **Data:**
  - EF Core 10 with PostgreSQL, one schema per module.
  - Dynamic item fields are stored in a `jsonb` document with a provider-specific query translator.
  - Complex-type JSON mapping is used for fixed structured data.
- **Multitenancy:** named query filters, a `SaveChanges` interceptor, and PostgreSQL row-level security.
- **Events:** a transactional outbox (`SKIP LOCKED` + `LISTEN/NOTIFY`) feeding Channels. Quartz.NET handles schedules.
- **Files:** content-addressed blob storage. An in-process processing pipeline with Tesseract OCR.
- **Search:** an app-maintained PostgreSQL full-text index across all data types, with pgvector for semantic search later.
- **Identity:** OpenIddict + ASP.NET Core Identity, API tokens, and a scope + ACL authorization model.
- **Item versioning** is optional per list (LST-11). File versions always exist for documents.
- **Dependencies:** only MIT / Apache-2.0 (or permissive with notice), see [dependency-licenses.md](dependency-licenses.md).

## 5. Roadmap

The roadmap (phases P0–P7 with the features in each phase) is maintained in
**[features.md](features.md#roadmap)**.

## 6. Decisions

| # | Question | Decision / recommendation |
|---|---|---|
| 1 | Frontend: React/TypeScript or Blazor? | **Deferred** (backend-only for now). Leaning **React/TS**: a larger extension-developer audience and a mature dynamic-module ecosystem. Parts of Papermerge's UI ideas can be reused |
| 2 | Deployment: self-hosted single-tenant, SaaS multi-tenant, or both? | **Decided (2026-09-24): multitenancy from the start.** Shared DB with `TenantId` on every row. Self-hosted = one default tenant |
| 3 | Extension trust: in-process only, or remote from day one? | **Decided (2026-09-24): in-process extensions first** (phase 2). Contracts are designed so remote extensions can be added in phase 6 |
| 4 | Database: PostgreSQL only, or also SQL Server/SQLite? | **Decided (2026-09-24, ADR-0009): SQLite by default, PostgreSQL optional**, everything through EF Core and working on both. Provider-specific features stay behind abstractions (see section 4) |
| 5 | License / business model | Decide early. It affects extension licensing (e.g. MIT core with a commercial marketplace) |
| 6 | Mobile / offline support | **Deferred** with the UI. Out of scope for v1. Keep the API sync-friendly (ETags, `modifiedSince`) |
| 7 | Dependency licenses | **Decided (2026-09-24):** MIT / Apache-2.0, plus BSD / PostgreSQL License with notice when there is no alternative. See [dependency-licenses.md](dependency-licenses.md) |
| 8 | Priorities | **Decided (2026-09-24): self-hosting and practicality first.** Minimal install = one PaperDotNet container (SQLite), everything else optional. Use mature in-process libraries rather than reinventing them |
