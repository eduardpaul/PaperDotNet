# PaperDotNet feature catalog

**Status:** draft for review (2026-09-24)

This is the product backlog. It is built from:
- all [ideas](../ideas/README.md)
- the useful parts of Papermerge ([papermerge-features.md](papermerge-features.md))
- the [architecture vision](architecture-vision.md)

Each feature has at least one end-user story. The technical approach is in
[technical-approach.md](technical-approach.md).

**Scope:** backend API only for now. UI stories describe what the API must enable.

## Legend

**Personas**

| Persona | Who |
|---|---|
| **Member** | Everyday user who stores documents and works with tasks and events |
| **Owner** | Workspace owner: sets up lists, fields, permissions |
| **Admin** | Tenant administrator |
| **Operator** | Person who self-hosts and runs the installation |
| **Developer** | Extension developer |
| **Integrator** | Developer, script or AI agent that uses the API, SDKs or MCP |

**Priority**

| Priority | Meaning |
|---|---|
| **MVP** | Needed for the first usable release |
| **Core** | Expected in a complete product |
| **Ext** | Optional or advanced |

**Phases**

| Phase | Content |
|---|---|
| **P0** | Foundation |
| **P1** | Lists engine, taxonomy and events |
| **P2** | Extension runtime v1 |
| **P3** | Documents (DMS) |
| **P4** | Tasks, calendar and notifications |
| **P5** | Collaboration, automation and integrations |
| **P6** | AI and semantic search |
| **P7** | Ecosystem |

The phases are described in [Roadmap](#roadmap).

**Source column:** `#NNNN` is an idea file. `PM §n` is a section of the
Papermerge feature catalog. `AV` is the architecture vision.

---

## 1. Platform & tenancy (PLT)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| PLT-01 | Two-container self-hosted install | As an **Operator**, I want to run `docker compose up` with only PaperDotNet and PostgreSQL, so that I get a fully working system (OCR included) without extra services | MVP | P0 | AV principle |
| PLT-02 | First-run bootstrap | As an **Operator**, I want the first start to create the default tenant and an admin account from environment variables, so that I can log in right away | MVP | P0 | PM §8 |
| PLT-03 | Multitenancy | As an **Operator**, I want to host several organizations in one installation with fully isolated data, files, settings and extensions, so that I can serve several customers or teams | MVP | P0 | #0005 |
| PLT-04 | Tenant resolution | As an **Admin**, I want my tenant reached through its own subdomain or custom domain, so that users land in the right organization | MVP | P0 | #0005 |
| PLT-05 | Tenant lifecycle | As an **Operator**, I want to create, suspend, export and delete tenants from the CLI and API, so that I can manage customers | Core | P0 | #0005 |
| PLT-06 | Tenant quotas | As an **Operator**, I want limits per tenant (storage, users, OCR and AI jobs, API rate), so that one tenant can't exhaust the server | Core | P5 | #0005 |
| PLT-07 | Workspaces | As an **Owner**, I want to create workspaces with members, lists and libraries, so that each team or project has its own space | MVP | P0 | AV §2 |
| PLT-08 | Configuration with validation | As an **Operator**, I want all settings to come from environment variables with clear errors at startup, so that misconfiguration fails fast | MVP | P0 | PM §10 |
| PLT-09 | Health, version and readiness endpoints | As an **Operator**, I want `/health/live`, `/health/ready` and `/version`, so that Docker or Kubernetes can monitor the service | MVP | P0 | PM §10 |
| PLT-10 | Observability | As an **Operator**, I want structured logs, traces and metrics (OpenTelemetry), tagged by tenant, so that I can troubleshoot problems | Core | P0 | building blocks |
| PLT-11 | Admin CLI | As an **Operator**, I want a `paperdotnet` CLI for migrations, tenants, users, reindexing and backup, so that I can automate operations | Core | P0 | PM §12 |
| PLT-12 | Backup & restore | As an **Operator**, I want to back up and restore the database and files with one documented command, so that I don't lose data | Core | P3 | PM §12 |
| PLT-13 | Tenant/workspace export & import | As an **Admin**, I want to export a workspace or tenant into an open format (files + JSON) and import it elsewhere, so that my data is portable | Ext | P7 | #0005 |

## 2. Identity & access (IAM)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| IAM-01 | Local accounts | As a **Member**, I want to sign in with username/email and password or a passkey, so that I can use the app without an external identity provider | MVP | P0 | PM §9 |
| IAM-02 | Built-in OAuth2/OIDC server | As an **Integrator**, I want standard OAuth2 flows (auth code + PKCE, client credentials), so that apps, SDKs and MCP clients can authenticate securely | MVP | P1 | #0003, #0004, ADR-0002 |
| IAM-03 | Personal API tokens / app passwords | As a **Member**, I want to create, list and revoke tokens with scopes and expiry, so that scripts, WebDAV and CalDAV clients can access my data | MVP | P0 | PM §9 |
| IAM-04 | External OIDC login | As an **Admin**, I want to connect my own identity provider (e.g. Keycloak, Entra ID, Google) per tenant, so that users use company SSO | Core | P5 | PM §9, #0005 |
| IAM-05 | Groups | As an **Admin**, I want groups with members, so that I can grant access to many users at once | MVP | P0 | PM §8 |
| IAM-06 | Roles with fine-grained scopes | As an **Admin**, I want roles built from scopes (e.g. `item.read`, `document.download`, `page.rotate`), so that I control exactly what people can do | MVP | P0 | PM §8 |
| IAM-07 | Permission inheritance | As an **Owner**, I want permissions to flow workspace → list → folder → item, with the option to break inheritance, so that I set access once and still handle exceptions | MVP | P1 | AV §4 |
| IAM-08 | Internal sharing | As a **Member**, I want to share an item, folder or list with users or groups at a chosen role, and see what is "shared with me", so that we can collaborate | Core | P5 | PM §8 |
| IAM-09 | Sharing links | As a **Member**, I want links with expiry, password and view/download/edit rights that I can revoke, so that I can share with people outside the organization | Core | P5 | #0015 |
| IAM-10 | File request links | As a **Member**, I want an upload-only link into a folder, so that clients can send me documents without seeing anything else | Core | P5 | #0015 |
| IAM-11 | Guest users | As an **Owner**, I want to invite external guests who only see what is shared with them, so that partners can work with us safely | Ext | P5 | #0015 |
| IAM-12 | Sharing policies | As an **Admin**, I want to allow or deny anonymous links, cap expiry and restrict guest domains, so that sharing follows company rules | Core | P5 | #0015 |
| IAM-13 | Extension-declared scopes | As a **Developer**, I want my extension to declare its own scopes, so that admins can assign them in roles | Core | P2 | AV §3.2 |

## 3. Lists engine (LST)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| LST-01 | Lists and libraries | As an **Owner**, I want to create lists (structured items) and libraries (items with files) in a workspace, so that I can store any kind of data | MVP | P1 | AV §2, #0001 |
| LST-02 | Content types | As an **Owner**, I want reusable content types (a named set of fields), several per list, so that e.g. Invoice and Contract can share a library | MVP | P1 | AV §2, PM §5 |
| LST-03 | Core field types | As an **Owner**, I want these field types, so that I can model my data precisely: text, number, money, boolean, date, datetime, choice, multi-choice, email, URL, person, lookup, managed metadata, keywords | MVP | P1 | PM §5 |
| LST-04 | Items CRUD with validation | As a **Member**, I want to create, read, update and delete items with server-side validation, so that data stays consistent | MVP | P1 | AV §2 |
| LST-05 | Bulk updates | As a **Member**, I want to change fields or tags on many items at once, so that I can organize quickly | Core | P1 | PM §5 |
| LST-06 | Folders | As a **Member**, I want optional folders inside lists and libraries, and to move items between them, so that I can organize the classic way | MVP | P1 | PM §3 |
| LST-07 | Home & Inbox | As a **Member**, I want a personal Home and an Inbox where new uploads land, so that I have a starting place and a triage queue | MVP | P1 | PM §3 |
| LST-08 | Relations (lookup fields) | As a **Member**, I want to link any item to any other (document ↔ task ↔ event ↔ note) and see backlinks, so that related work stays connected | MVP | P1 | AV §2 |
| LST-09 | View definitions | As an **Owner**, I want saved views (columns, filter, sort, group-by, layout: table, board, calendar, gallery), so that each audience sees the list the right way | Core | P1 | AV §2 |
| LST-10 | Query, filter, sort, page | As an **Integrator**, I want to filter, sort, select and page items with a consistent Graph-style syntax on every list, so that I don't need custom endpoints | MVP | P1 | #0003, PM §6 |
| LST-11 | Optional version history | As an **Owner**, I want to choose per list whether changes are versioned (off / major / major+minor, max versions), so that important data keeps history and bulk data stays lean | Core | P1 | #0010 |
| LST-12 | Version list, compare, restore | As a **Member**, I want to see an item's versions, compare them and restore an old one, so that I can undo mistakes | Core | P1 | #0010, PM §1 |
| LST-13 | Recycle bin (soft delete) | As a **Member**, I want deleted items to go to a recycle bin I can restore from, so that deletes are recoverable | Core | P1 | PM §1 |
| LST-14 | Audit log | As an **Admin**, I want an audit log of who changed what and when (including automations, extensions and AI), so that I can trace every change | Core | P1 | PM §10 |
| LST-15 | Optimistic concurrency | As a **Member**, I want to be warned when someone else changed an item since I loaded it, so that I don't overwrite their work | MVP | P1 | #0006 |
| LST-16 | List templates | As an **Owner**, I want to create lists from templates (Documents, Tasks, Calendar, Contacts, Notes, or extension-provided), so that setup is quick | Core | P2 | AV §3.2 |
| LST-17 | Comments & activity | As a **Member**, I want to comment on any item, @mention people and see an activity timeline, so that discussion stays with the item | Ext | P5 | top-10 list |
| LST-18 | Notes content type (Markdown) | As a **Member**, I want Markdown notes as items with links and tags, so that knowledge sits next to documents and tasks | Ext | P7 | #0007 |

## 4. Taxonomy, tags & navigation (TAX)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| TAX-01 | Term store | As an **Admin**, I want a tenant term store (groups → term sets → hierarchical terms), so that the organization shares one vocabulary | MVP | P1 | #0008 |
| TAX-02 | Term management | As an **Admin**, I want synonyms, multilingual labels, colors and descriptions, and to deprecate, merge, move and reuse terms, so that the vocabulary stays clean over time | Core | P1 | #0008 |
| TAX-03 | Open and closed term sets | As an **Owner**, I want closed sets (pick only) and open sets (users can add), so that I choose between governance and flexibility | Core | P1 | #0008 |
| TAX-04 | Folksonomy keywords | As a **Member**, I want to add free tags on the fly with autocomplete, so that tagging is fast | MVP | P1 | #0008, PM §4 |
| TAX-05 | Promote keywords to taxonomy | As an **Admin**, I want to see popular keywords and promote them into a term set, so that good tags become official | Ext | P5 | #0008 |
| TAX-06 | Tag any item | As a **Member**, I want to tag documents, tasks, events, notes, folders and any list item the same way, so that tags work as one system | MVP | P1 | #0008 |
| TAX-07 | Hierarchical tag filtering | As a **Member**, I want filtering by a parent term (e.g. *Finance*) to include its children, so that broad filters work | Core | P1 | #0008 |
| TAX-08 | Smart folders | As a **Member**, I want virtual folders defined by rules on tags, fields, type and relative dates (across all data types), so that I can navigate by meaning instead of location | Core | P5 | #0020 |
| TAX-09 | Drop-to-classify | As a **Member**, I want moving an item into a smart folder to apply that folder's tags and field values, so that filing and classifying are one action | Core | P5 | #0020 |
| TAX-10 | Metadata navigation | As an **Owner**, I want smart folders that group by fields or term hierarchy into automatic sub-folders (e.g. Invoices → Year → Counterparty), so that large libraries are easy to browse | Core | P5 | #0020 |
| TAX-11 | Term sets from extensions & import | As an **Admin**, I want to import term sets (CSV) and get them from extensions, so that I start from ready vocabularies | Ext | P5 | #0008 |

## 5. Events, automation & jobs (EVT)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| EVT-01 | Before event handlers | As a **Developer**, I want synchronous `…ing` handlers (ItemAdding, ItemUpdating, …) that can change or cancel an operation with a message, so that I can enforce business rules | MVP | P1 | #0012 |
| EVT-02 | After event handlers | As a **Developer**, I want `…ed` handlers that run after commit, either in the request or in the background with retries, so that I can trigger side effects reliably | MVP | P1 | #0012 |
| EVT-03 | Handler registration & ordering | As a **Developer**, I want to register handlers per tenant, workspace, list, list template or content type, with a sequence number and conditions, so that handlers run only where they belong | Core | P2 | #0012 |
| EVT-04 | Reliable event delivery (outbox) | As an **Admin**, I want every change to produce events delivered at least once, even after a crash, so that integrations and automations never miss changes | MVP | P1 | AV §4 |
| EVT-05 | Background jobs & schedules | As a **Developer**, I want to schedule jobs (cron, delayed, recurring) that survive restarts, so that reminders, recurrence and maintenance run on time | MVP | P1 | PM §7 |
| EVT-06 | Long-running operations | As an **Integrator**, I want long operations (OCR, bulk updates, exports, reindexing) to return an operation I can poll or be notified about, so that I get their results reliably | Core | P1 | PM §7 |
| EVT-07 | Rules ("if this then that") | As an **Owner**, I want simple rules like "when a document tagged *Invoice* arrives, move it to Finance and create a task", so that routine work is automatic | Core | P5 | #0009 |
| EVT-08 | Workflows | As an **Admin**, I want multi-step, long-running workflows (approvals, escalations, retention reviews) based on Elsa, so that business processes run inside the system | Ext | P5 | #0009 |
| EVT-09 | Automation activities from extensions | As a **Developer**, I want to contribute triggers and actions to rules and workflows, so that my extension can be automated | Ext | P5 | #0009 |

## 6. Extensibility (EXT)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| EXT-01 | Extension manifest | As a **Developer**, I want a language-neutral manifest declaring what my extension contributes and needs, so that the host can validate, install and sandbox it | MVP | P2 | AV §3.1, #0011 |
| EXT-02 | In-process .NET extensions | As a **Developer**, I want to package a .NET extension that the host loads in isolation and can upgrade without breaking others, so that I can extend the app with full power | MVP | P2 | AV §3.3 |
| EXT-03 | Per-tenant install & settings | As an **Admin**, I want to enable, configure and disable extensions per tenant, so that each organization chooses its features | MVP | P2 | #0005 |
| EXT-04 | Extension points (server) | As a **Developer**, I want to contribute field types, content types, list templates, commands, event handlers, file processors, jobs, API endpoints, search indexers, providers, MCP tools and scopes, so that I can extend any part of the system | MVP | P2 | AV §3.2 |
| EXT-05 | Extension SDK & test harness | As a **Developer**, I want a NuGet SDK with attributes, analyzers and a local test host, so that building and testing extensions is quick | Core | P2 | #0011 |
| EXT-06 | Built-in apps as extensions | As a **Developer**, I want Documents, Tasks and Calendar built on the public SDK, so that I can rely on the SDK being complete | MVP | P3–P4 | AV §3.3 |
| EXT-07 | Extension data storage | As a **Developer**, I want my extension to store its own data (its own tables or list-based storage) in the tenant's isolation boundary, so that I don't manage a database | Core | P2 | AV §3 |
| EXT-08 | Node.js and Python extensions | As a **Developer**, I want to write extensions in Node.js or Python, run and supervised by the host, with the same before/after handlers, so that I can use those ecosystems (e.g. Python ML) | Ext | P7 | #0011 |
| EXT-09 | Remote extensions (webhooks) | As a **Developer**, I want to host an extension as my own service that receives signed webhooks and calls the API with a scoped token, so that I can integrate from any language | Ext | P7 | AV §3.3 |

## 7. Documents / DMS (DOC)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| DOC-01 | Upload | As a **Member**, I want to upload PDF, TIFF, JPEG and PNG files (streamed, with size limits) into a library or my Inbox, so that I can digitize paper | MVP | P3 | PM §1 |
| DOC-02 | File type detection | As a **Member**, I want uploads recognized by content, not by extension, so that wrong or missing extensions don't break processing | MVP | P3 | PM §1 |
| DOC-03 | File versions | As a **Member**, I want every change to a file (page operations, OCR, re-upload) to create a new version while the original is always kept, so that nothing is ever lost | MVP | P3 | PM §1 |
| DOC-04 | Thumbnails & page images | As a **Member**, I want thumbnails and page previews generated automatically, so that I recognize documents at a glance | MVP | P3 | PM §1 |
| DOC-05 | Page operations | As a **Member**, I want to delete, reorder and rotate pages, so that I can fix scanning mistakes | MVP | P3 | PM §2 |
| DOC-06 | Move, merge & extract pages | As a **Member**, I want to move pages between documents, merge documents and extract pages into new documents, so that I can fix mixed-up scans | Core | P3 | PM §2, merge-docs |
| DOC-07 | OCR | As a **Member**, I want scanned documents OCRed in my chosen language (automatically or on demand), so that their text becomes searchable | MVP | P3 | PM §7 |
| DOC-08 | Searchable PDF download | As a **Member**, I want to download the PDF with the OCR text layer, so that I can search it in any PDF reader | Core | P3 | PM §1 |
| DOC-09 | Processing status | As a **Member**, I want live status for OCR and processing (scheduled, running, done, failed), so that I know when a document is ready | Core | P3 | PM §7 |
| DOC-10 | Exact duplicate detection | As a **Member**, I want to be warned (or blocked, per library rule) when I upload a file that already exists, so that I don't store the same document twice | Core | P3 | #0014 |
| DOC-11 | Deduplicated storage | As an **Operator**, I want identical files stored once, so that storage use stays low | Core | P3 | #0014 |
| DOC-12 | Near-duplicate detection | As a **Member**, I want to see documents very similar to this one (e.g. re-scans), so that I can merge or clean them up | Ext | P6 | #0014 |
| DOC-13 | Email to inbox | As a **Member**, I want to forward emails to a personal or group address, so that their attachments arrive as documents in my Inbox | Core | P5 | #0002 |
| DOC-14 | Path templates | As an **Owner**, I want documents automatically renamed and filed from their metadata (e.g. `/Finance/{Year}/{Counterparty}`), so that the structure maintains itself | Ext | P5 | PM §5 |
| DOC-15 | Storage providers | As an **Operator**, I want local disk by default and S3-compatible storage optionally, so that I pick storage that fits my setup | Core | P3 | PM §11 |

## 8. Tasks (TSK)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| TSK-01 | Task content type | As a **Member**, I want tasks with title, description, status, priority, assignees, start and due date, and a checklist, so that I can manage my work | MVP | P4 | AV §2 |
| TSK-02 | Subtasks & dependencies | As a **Member**, I want subtasks and "blocked by" links, so that I can break down and sequence work | Core | P4 | AV §2 |
| TSK-03 | My tasks & due views | As a **Member**, I want views like "My tasks", "Due this week" and "Overdue" across all lists, so that I know what to do next | MVP | P4 | AV §2, #0020 |
| TSK-04 | Board (kanban) view definition | As a **Member**, I want a board view grouped by status, so that I can track progress visually (once a UI exists) | Core | P4 | AV §2 |
| TSK-05 | Recurring tasks | As a **Member**, I want tasks that repeat on a schedule, so that routine work isn't forgotten | Core | P4 | AV §2 |
| TSK-06 | Tasks from documents | As a **Member**, I want to create a task linked to a document (manually, by rule or from AI-extracted dates), so that "pay this invoice" is tracked | Core | P4 | #0017, #0009 |

## 9. Calendar (CAL)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| CAL-01 | Event content type | As a **Member**, I want events with start, end, all-day, location, attendees and reminders, so that I can plan my time | MVP | P4 | AV §2 |
| CAL-02 | Recurrence | As a **Member**, I want repeating events with exceptions (RRULE), so that I can model real schedules | MVP | P4 | AV §2 |
| CAL-03 | Calendar views & time-range queries | As an **Integrator**, I want to query events and due tasks for a time range, with recurrences expanded, so that calendars can be displayed and synced | MVP | P4 | AV §2 |
| CAL-04 | iCal import/export & feeds | As a **Member**, I want to import `.ics` files and subscribe to a read-only calendar feed, so that I can exchange calendars with other tools | Core | P4 | AV §3.2 |
| CAL-05 | CalDAV (events & tasks) | As a **Member**, I want my calendars and task lists to sync two-way with iOS, Android (DAVx⁵) and Thunderbird, so that I can use native apps offline | Core | P4 | #0019 |
| CAL-06 | CardDAV (contacts) | As a **Member**, I want a contacts list that syncs with my phone's address book, so that contacts live in the same system | Ext | P5 | #0019 |

## 10. Notifications (NTF)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| NTF-01 | In-app notification inbox | As a **Member**, I want a notification list with read/unread, so that I see what needs my attention | MVP | P4 | #0016 |
| NTF-02 | Reminders | As a **Member**, I want reminders for due tasks and upcoming events, so that I don't miss deadlines | MVP | P4 | #0016 |
| NTF-03 | Alerts / subscriptions | As a **Member**, I want to follow an item, folder, list, smart folder or search and be notified of changes, immediately or as a digest, so that I stay informed without checking | Core | P4 | #0016 |
| NTF-04 | Channels | As a **Member**, I want notifications by email, webhook or self-hosted push (ntfy, Gotify), so that they reach me where I am | Core | P4 | #0016 |
| NTF-05 | Notification preferences | As a **Member**, I want to choose channels per notification type, quiet hours and digest frequency, so that I'm not overwhelmed | Core | P4 | #0016 |
| NTF-06 | Channel providers from extensions | As a **Developer**, I want to add channels (Slack, Teams, Matrix, Telegram), so that organizations use their chat tools | Ext | P5 | #0016 |

## 11. Search (SRC)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| SRC-01 | Unified full-text search | As a **Member**, I want one search across documents (including OCR text), tasks, events, notes and any list item, so that I find things without knowing where they are | MVP | P1–P3 | #0013, PM §6 |
| SRC-02 | Query syntax | As a **Member**, I want phrases, OR, NOT and prefix search, so that I can be precise | Core | P1 | PM §6 |
| SRC-03 | Filters & facets | As a **Member**, I want to narrow results by type, tags (hierarchical), owner, dates and fields, with counts, so that I can drill down | Core | P1 | #0013, PM §6 |
| SRC-04 | Security trimming | As an **Admin**, I want search to only ever return items the user may see, so that search never leaks data | MVP | P1 | #0013 |
| SRC-05 | Language-aware search | As a **Member**, I want stemming in the document's language, so that "invoices" finds "invoice" | Core | P3 | PM §6 |
| SRC-06 | Searchable fields from extensions | As a **Developer**, I want to declare which fields of my content type are indexed and with what weight, so that my data is searchable | Core | P2 | #0013 |
| SRC-07 | Semantic (vector) search | As a **Member**, I want to find documents by meaning, so that I find them even when the wording differs or the OCR is poor | Ext | P6 | #0013 |
| SRC-08 | Hybrid ranking | As a **Member**, I want keyword and semantic results combined into one ranked list, so that the best matches come first | Ext | P6 | #0013 |
| SRC-09 | Page-level hits | As a **Member**, I want results to point to the page of a document that matched, so that I jump straight to it | Ext | P6 | #0013 |
| SRC-10 | Reindexing | As an **Operator**, I want to rebuild the index (per tenant, in the background, with progress), so that schema or model changes take effect | Core | P3 | #0013 |

## 12. API, SDKs & integrations (API)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| API-01 | Graph-style REST API | As an **Integrator**, I want resource URLs (`/v1.0/workspaces/{id}/lists/{id}/items`), Graph-style query options, paging links and errors, so that the API feels familiar and consistent | MVP | P0–P1 | #0003 |
| API-02 | OpenAPI description | As an **Integrator**, I want a complete, versioned OpenAPI 3.1 document, including extension endpoints, so that I can generate clients and explore the API | MVP | P0 | #0003 |
| API-03 | Generated SDKs | As an **Integrator**, I want official SDKs for C#, TypeScript and Python generated from the OpenAPI description, so that I can integrate quickly in my language | Core | P5 | #0003 |
| API-04 | Batch requests | As an **Integrator**, I want to send several operations in one `$batch` request, so that clients stay fast on slow networks | Ext | P5 | #0003 |
| API-05 | Delta sync | As an **Integrator**, I want `/delta` endpoints with tokens that return only changes and deletions since the last call, so that offline clients and sync tools stay current cheaply | Core | P5 | #0003, #0006 |
| API-06 | Webhooks / change notifications | As an **Integrator**, I want to subscribe to changes on a resource and receive signed webhooks, so that my systems react in real time | Core | P5 | #0003, #0016 |
| API-07 | Live events stream | As an **Integrator**, I want a server-sent events stream for my changes and job status, so that a future UI updates live | Core | P3 | PM §7 |
| API-08 | MCP server | As an **Integrator** using an AI assistant, I want an MCP endpoint with tools for searching, reading, creating and updating items, documents, tasks and events, so that assistants can work with my data within my permissions | Core | P5 | #0004 |
| API-09 | MCP tools from extensions | As a **Developer**, I want my extension's commands to appear as MCP tools automatically, so that AI agents can use my features | Core | P5 | #0004 |
| API-10 | WebDAV for libraries | As a **Member**, I want to mount libraries as a network drive and open or save files from desktop apps, with saves creating versions, so that I can work with my usual tools | Core | P5 | #0018 |
| API-11 | Obsidian vault sync | As a **Member**, I want my Obsidian vault to sync with a Notes list (frontmatter → fields, tags → terms, links → relations, attachments → documents), so that my notes join the rest of my data | Ext | P7 | #0007 |
| API-12 | Offline-ready API for mobile | As a **Member** on the go, I want a future mobile app to work offline and sync via delta, ETags and resumable uploads, so that I can capture and read documents without a connection | Ext | P7 | #0006 |

## 13. AI (AI)

| ID | Feature | User story | Prio | Phase | Source |
|---|---|---|---|---|---|
| AI-01 | AI provider configuration | As an **Admin**, I want to configure an AI provider per tenant (local model or cloud) or none, so that AI features respect privacy and cost choices | Core | P6 | #0017 |
| AI-02 | Classification & auto-tagging | As a **Member**, I want uploaded documents to get a suggested content type and tags, so that filing takes one click | Core | P6 | #0017, #0008 |
| AI-03 | Metadata extraction | As a **Member**, I want fields like invoice number, amount, due date and counterparty filled from the document text as suggestions with confidence, so that I don't type metadata | Core | P6 | #0017 |
| AI-04 | Summaries & key dates | As a **Member**, I want a summary and extracted key dates, optionally turned into tasks or events, so that deadlines from documents are tracked | Ext | P6 | #0017 |
| AI-05 | Ask your documents | As a **Member**, I want to ask questions and get answers with citations to documents and pages, so that I find information instead of files | Ext | P6 | #0013 |
| AI-06 | AI usage limits & audit | As an **Admin**, I want quotas, caching and an audit of what was sent to which model, so that AI use is controlled | Core | P6 | #0017 |

---

## Roadmap

| Phase | Goal | Features |
|---|---|---|
| **P0 Foundation** | Deployable, secure, multi-tenant skeleton | PLT-01…11, IAM-01, IAM-03, IAM-05, IAM-06, API-01 (conventions), API-02 |
| **P1 Lists engine, taxonomy & events** | Store and organize any data | IAM-02 (OpenIddict, passkeys), LST-01…15, TAX-01…04, TAX-06, TAX-07, IAM-07, EVT-01, EVT-02, EVT-04…06, SRC-01…04, API-01 (queries) |
| **P2 Extension runtime v1** | Everything below is built as extensions | EXT-01…05, EXT-07, EVT-03, IAM-13, LST-16, SRC-06 |
| **P3 Documents** | Papermerge-level DMS | DOC-01…11, DOC-15, SRC-05, SRC-10, API-07, PLT-12, EXT-06 |
| **P4 Tasks, calendar & notifications** | Productivity suite | TSK-01…06, CAL-01…05, NTF-01…05 |
| **P5 Collaboration, automation & integrations** | Share, automate, connect | IAM-04, IAM-08…12, TAX-05, TAX-08…11, EVT-07…09, DOC-13, DOC-14, LST-17, CAL-06, NTF-06, API-03…06, API-08…10, PLT-06 |
| **P6 AI & semantic search** | Understand documents | AI-01…06, SRC-07…09, DOC-12 |
| **P7 Ecosystem** | Other languages, remote extensions, sync clients | EXT-08, EXT-09, LST-18, API-11, API-12, PLT-13 |

## Phase 0 status

Implemented in the solution skeleton (see [ADR-0006](adr/0006-phase-0-simplifications.md) for deferrals):

| Feature | Status |
|---|---|
| PLT-01 Two-container install | ✅ `Dockerfile` + `deploy/docker-compose.yml` |
| PLT-02 First-run bootstrap | ✅ default tenant + admin from configuration |
| PLT-03, PLT-04 Multitenancy & resolution | ✅ host mapping, host template, header (opt-in), claim, default; EF filters + write guard (RLS in P1) |
| PLT-05 Tenant lifecycle | 🟡 CLI: list, create, suspend, activate (export/delete later) |
| PLT-07 Workspaces | ✅ CRUD, members, owners, ETags, soft delete |
| PLT-08 Configuration | ✅ `PAPERDOTNET__…` environment variables, validated options |
| PLT-09 Health & version | ✅ `/health/live`, `/health/ready`, `/version` |
| PLT-10 Observability | ✅ OpenTelemetry traces, metrics and logs (OTLP when configured) |
| PLT-11 Admin CLI | ✅ `migrate`, `bootstrap`, `tenant`, `user`, `healthcheck` |
| IAM-01 Local accounts | 🟡 passwords + lockout; passkeys and MFA in P1 |
| IAM-03 API tokens | ✅ scoped, expiring, revocable, hashed |
| IAM-05, IAM-06 Groups, roles & scopes | ✅ built-in Administrator/Member roles, custom roles, group assignment |
| API-01 Graph-style conventions | ✅ `/v1.0`, keyset paging + `@odata.nextLink`, ProblemDetails with `code`, ETag/If-Match |
| API-02 OpenAPI | ✅ `/openapi/v1.json` with bearer security scheme |

## Idea → feature mapping

| Idea | Mapped to |
|---|---|
| [0001](../ideas/0001-extensible-dms-productivity-platform.md) Extensible DMS + productivity platform | Whole catalog; LST-01, EXT-01…09 |
| [0002](../ideas/0002-email-to-inbox.md) Email to inbox | DOC-13 |
| [0003](../ideas/0003-graph-style-api-and-generated-sdks.md) Graph-style API & SDKs | API-01…06, LST-10, IAM-02 |
| [0004](../ideas/0004-mcp-server.md) MCP server | API-08, API-09, IAM-02 |
| [0005](../ideas/0005-ootb-multitenancy.md) Multitenancy | PLT-03…06, PLT-13, EXT-03, IAM-04 |
| [0006](../ideas/0006-mobile-offline-sync.md) Mobile offline sync | API-05, API-12, LST-15, CAL-05 (mobile app itself deferred with UI) |
| [0007](../ideas/0007-obsidian-vault-sync.md) Obsidian vault sync | API-11, LST-18 |
| [0008](../ideas/0008-taxonomy-folksonomy-metadata-service.md) Taxonomy & folksonomy | TAX-01…07, TAX-11 |
| [0009](../ideas/0009-automation-rules-engine-elsa.md) Automation with Elsa | EVT-07…09, TSK-06 |
| [0010](../ideas/0010-optional-version-history.md) Optional version history | LST-11, LST-12 |
| [0011](../ideas/0011-extensions-in-nodejs-and-python.md) Node.js & Python extensions | EXT-01 (language-neutral manifest), EXT-08 |
| [0012](../ideas/0012-sharepoint-style-event-handlers.md) SharePoint-style event handlers | EVT-01…04 |
| [0013](../ideas/0013-unified-fulltext-and-vector-search.md) Full-text & vector search | SRC-01…10, AI-05 |
| [0014](../ideas/0014-duplicate-detection-content-hashing.md) Duplicate detection | DOC-10…12 |
| [0015](../ideas/0015-sharing-links-and-guest-access.md) Sharing links & guests | IAM-09…12 |
| [0016](../ideas/0016-notifications-alerts-subscriptions.md) Notifications | NTF-01…06, API-06 |
| [0017](../ideas/0017-ai-metadata-extraction.md) AI metadata extraction | AI-01…04, AI-06, TSK-06 |
| [0018](../ideas/0018-webdav-access-to-libraries.md) WebDAV | API-10 |
| [0019](../ideas/0019-caldav-carddav-server.md) CalDAV / CardDAV | CAL-05, CAL-06 |
| [0020](../ideas/0020-smart-folders.md) Smart folders | TAX-08…10, TSK-03 |
