# Papermerge feature catalog: what to port to PaperDotNet

Source surveyed: [papermerge/papermerge-core](https://github.com/papermerge/papermerge-core)
(Apache-2.0, v3.6 dev, commit `5ed5dbf`, 2026-09-18), plus the companion repos
`path-tmpl-worker`, `i3worker` and `documentation`.

Papermerge is a document management system (DMS) for scanned documents. Its backend
is Python (FastAPI, async SQLAlchemy, PostgreSQL, Alembic, Celery/Redis). Its
frontend is React, Mantine and Redux in a monorepo. OCR, thumbnails, search
indexing and path templates run in separate Celery **worker** services.

**How to use this list:** tick `[x]` next to each feature you want ported. The
**Tier** column is a suggestion:

- **MVP**: needed for a usable DMS.
- **Core**: expected in a full product.
- **Ext**: optional or advanced.

The **.NET hint** column names a likely library or approach. Only MIT/Apache-2.0 (or equivalent permissive) libraries are suggested, see [dependency-licenses.md](dependency-licenses.md).

---

## 1. Documents & files

| Pick | Feature | What it does in Papermerge | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Document upload | `POST /documents/upload`, max size limit (`max_file_size_mb`), creates document + version 1 | MVP | ASP.NET Core minimal API / controllers, `IFormFile` streaming |
| [ ] | Supported formats | PDF, TIFF, JPEG, PNG (images converted to PDF via img2pdf) | MVP | PDFsharp (MIT); SkiaSharp (MIT) for JPEG/PNG; LibTiff.Net (BSD-3) for TIFF |
| [ ] | MIME detection | Content-sniffing via libmagic, not by extension | MVP | Own magic-byte sniffer for the 4 formats; or `Mime` (HeyRed, MIT, wraps libmagic) |
| [ ] | Page count detection | Reads page count from PDF/TIFF | MVP | PdfPig (Apache-2.0); LibTiff.Net for TIFF |
| [ ] | Document versioning | Every page operation or OCR creates a new version, and v1 (the original) is always kept (non-destructive) | MVP | Domain model + EF Core |
| [ ] | List / view / download versions | `GET /documents/{id}/versions`, `/last-version`, `/document-versions/{id}/download-url` | MVP | — |
| [ ] | Download with OCR text layer | Download the PDF with the OCRed text overlaid (searchable PDF) | Core | Produced by OCR worker |
| [ ] | Per-version language | `GET/PATCH /document-versions/{id}/lang` (OCR language) | Core | — |
| [ ] | Rename document | Update title | MVP | — |
| [ ] | Thumbnails & page previews | Document thumbnail + page images at a configured size, with a thumbnail status endpoint | MVP | PDFtoImage or Docnet.Core (MIT, bundle PDFium) + SkiaSharp (MIT) |
| [ ] | Frontend-side rendering | Since 3.5.2, previews are rendered in the browser (pdf.js) to speed up large PDFs | Core | pdf.js in the SPA |
| [ ] | Soft delete / archive columns | `archived_at/by`, `deleted_at/by` audit columns on entities | Core | EF Core global query filters |

## 2. Page management (all non-destructive, each creates a new version)

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Delete pages | Remove blank or unwanted pages | MVP | PDFsharp (MIT) |
| [ ] | Reorder pages | Drag-and-drop reordering | MVP | same |
| [ ] | Rotate pages | 90/180/270 rotation | MVP | same |
| [ ] | Move pages between documents | `POST /pages/move`, either append/prepend to the target or **replace** the target's pages ("merge documents") | Core | same |
| [ ] | Extract pages to new document(s) | `POST /pages/extract`, into one new document or one per page, placed in a target folder | Core | same |
| [ ] | Batch page operations | `POST /pages/` applies a list of operations at once | Core | — |

## 3. Nodes: folders & organization

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Hierarchical folders | Documents and folders are both "nodes" in a tree. Supports create, list children, rename and delete | MVP | EF Core self-referencing entity, recursive CTE for paths |
| [ ] | Move nodes | `POST /nodes/move` (bulk) | MVP | — |
| [ ] | Breadcrumbs / folder details | `GET /folders/{id}` | MVP | — |
| [ ] | Special folders: Home & Inbox | Every user/group gets a Home and an Inbox folder. Uploads land in Inbox | MVP | Created on user/group creation |
| [ ] | Group Home/Inbox | Folders owned by groups (`/users/group-homes`, `/group-inboxes`) | Core | — |
| [ ] | Node events | Deleting nodes enqueues cleanup of files and thumbnails (async) | Core | Domain events + background queue |

## 4. Tags

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Tag CRUD | Colored tags (bg/fg color, description, pinned), paginated list | MVP | — |
| [ ] | Assign tags to nodes | Replace, add or remove tags on documents **and folders** (`/nodes/{id}/tags` GET/POST/PATCH/DELETE) | MVP | — |
| [ ] | Tag ownership | Tags owned by a user or a group | Core | — |

## 5. Document types (categories) & custom fields (metadata)

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Document types | Categories with an ordered set of custom fields, grouped listing, owner (user/group) | Core | — |
| [ ] | Custom fields | Typed metadata fields with usage counts | Core | EF Core JSON columns or EAV table |
| [ ] | Field types | text, short_text, integer, number, monetary, boolean, date, datetime, yearmonth, email, url, select, multiselect | Core | Type registry / strategy pattern |
| [ ] | Assign type to document | `PATCH /documents/{id}/type` | Core | — |
| [ ] | Edit field values | Per-document and **bulk** values update | Core | — |
| [ ] | Documents-by-type table view | `/documents/type/{id}/` shows a table with custom fields as columns, sortable and filterable | Core | — |
| [ ] | Path templates | `DocumentType.path_template` auto-moves and renames documents based on their metadata (separate `path-tmpl-worker`) | Ext | Background service + template engine (Fluid, MIT) |

## 6. Search

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Full-text search | PostgreSQL FTS (`tsvector` columns kept current by SQL triggers), with per-language config | MVP | Npgsql FTS (`EF.Functions.ToTsVector`) |
| [ ] | Query syntax | Phrases, `|` OR, grouping `( )` | Core | `websearch_to_tsquery` |
| [ ] | Structured filters | Tags (all/any/not), category (any/not), owner (eq/ne), custom-field operators (eq, ne, gt/gte/lt/lte, ilike, in, any/all, is_null, is_checked…) | Core | Dynamic LINQ expression builder |
| [ ] | Sort & paginate results | Sort by field/direction, per-user `search_lang` preference | Core | — |
| [ ] | External search engine (Solr) | Legacy `i3worker` syncs DB → Solr | Ext | Skip, or use Meilisearch (MIT) / OpenSearch (Apache-2.0) via a background indexer |

## 7. OCR & background processing

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | OCR | Tesseract-based (via OCRmyPDF in the worker), creates a new version with a text layer, and the text is indexed | MVP | Tesseract (Apache-2.0) in a worker container: CLI or `Tesseract` NuGet wrapper, text layer merged with PDFsharp. (OCRmyPDF excluded: MPL-2.0 + Ghostscript AGPL) |
| [ ] | Manual OCR trigger | `POST /tasks/ocr` | Core | — |
| [ ] | Auto vs manual OCR preference | User setting to OCR on upload or only on demand | Core | — |
| [ ] | OCR languages | Many Tesseract langs, default language setting (`default_lang`, default `deu`) | Core | — |
| [ ] | Real-time OCR status | Unknown / scheduled / in-progress / success / failed indicator in UI | Core | SignalR |
| [ ] | Task queue | Celery + Redis, with named queues for OCR, thumbnails, upload processing and file cleanup | MVP | Outbox + `Channel<T>` + `BackgroundService`; Wolverine (MIT) / Quartz.NET (Apache-2.0) |

## 8. Users, groups, roles & permissions

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Users | CRUD, change password, `/users/me`, superuser | MVP | ASP.NET Core Identity |
| [ ] | Groups | CRUD, membership, group-owned resources | Core | — |
| [ ] | Roles with fine-grained scopes | Roles are sets of ~70 scopes (e.g. `node.view`, `document.download.all_versions`, `page.rotate`, `tag.select`) and can be assigned to users. The UI edits them as a checkbox tree | Core | Policy-based authorization, one policy per scope |
| [ ] | Ownership model | Resources owned by a user **or** a group (`owner_type`/`owner_id`) | Core | — |
| [ ] | Sharing | Share documents/folders with users and/or groups with specific roles. Includes a "Shared with me" listing and per-node access management (`/shared-nodes/access/{id}`) | Core | — |
| [ ] | Superuser bootstrap | Admin user created from env vars | MVP | Seed on startup |

## 9. Authentication

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | JWT bearer auth | Tokens issued by a separate `auth-server` (username/password) | MVP | `Microsoft.AspNetCore.Authentication.JwtBearer` |
| [ ] | API tokens | Personal access tokens with create, list, delete and expiry (`/api-tokens`) | Core | Hashed tokens + custom auth handler |
| [ ] | OIDC / SSO | Keycloak and Zitadel setups supported | Ext | `AddOpenIdConnect` |
| [ ] | Remote-user (reverse-proxy) auth | Trusts `X-Forwarded-User/Groups/Roles/Name/Email` headers | Ext | Custom `AuthenticationHandler` |
| [ ] | LDAP / OAuth2 (Google, GitHub) | Provided by the external auth-server | Ext | `Novell.Directory.Ldap.NETStandard`, OAuth providers |

## 10. Audit, preferences & system

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Audit log | INSERT/UPDATE/DELETE records with user and context, paginated list + detail view (new in 3.6) | Core | EF Core `SaveChanges` interceptor |
| [ ] | Audit columns | `created_by/at`, `updated_by/at` on all entities | MVP | EF Core interceptor |
| [ ] | User preferences | UI language, timezone, date/timestamp/number format, theme (light/dark), default document language, search language | Core | JSON column per user |
| [ ] | System preferences | Admin-level defaults (`/preferences/system`) | Core | — |
| [ ] | Liveness probe & version | `/probe`, `/version`, `/scopes` endpoints | MVP | ASP.NET Core Health Checks |
| [ ] | Redis cache (optional) | Caching layer, off by default | Ext | HybridCache; Garnet (MIT, Redis-compatible) as L2 |
| [ ] | Multitenant prefix | Storage/key prefix for multi-tenant deployments | Ext | — |

## 11. Storage

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Local filesystem storage | `media_root` | MVP | `IFileStorage` abstraction |
| [ ] | S3-compatible / Cloudflare R2 | Object storage backend | Ext | AWSSDK.S3 |
| [ ] | CloudFront signed URLs | Signed download URLs (`sign_url` CLI) | Ext | AWSSDK.CloudFront |

## 12. API & tooling

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | OpenAPI REST API | Fully documented API with standard pagination, sorting and filtering params | MVP | Built-in OpenAPI + Scalar/Swagger UI |
| [ ] | Server CLI | Typer commands for users, groups, roles/perms, tokens, docs, OCR re-run and search | Core | `System.CommandLine` |
| [ ] | Client CLI (`papermerge-cli`) | Import/upload from a local folder, download, search via REST | Ext | Separate .NET tool |
| [ ] | DB migrations | Alembic | MVP | EF Core Migrations |
| [ ] | Docker images / compose | Standard and OIDC (Keycloak) compose setups, nginx + supervisord | Core | Dockerfile + compose |
| [ ] | Backup / restore | Documented procedure (DB + media) | Ext | — |

## 13. Web UI (frontend)

| Pick | Feature | What it does | Tier | .NET hint |
|---|---|---|---|---|
| [ ] | Desktop-like file browser ("commander") | Folder navigation, list and tile views, multi-select, context menus, drag & drop | MVP | React (reuse) or Blazor |
| [ ] | Dual-panel mode | Two side-by-side panels (commander, viewer, or one of each), drag & drop between them | Core | — |
| [ ] | Document viewer | Page view, thumbnails panel, page selection, OCR text selectable | MVP | pdf.js |
| [ ] | Drag & drop upload | Upload into the current folder | MVP | — |
| [ ] | Admin screens | Users, groups, roles, tags, custom fields, document types, API tokens, audit log | Core | — |
| [ ] | i18n | Multiple UI languages | Core | i18next (React) / `IStringLocalizer` (Blazor) |
| [ ] | Themes | Light / dark | Ext | — |

## 14. Documented but removed or planned upstream (not in current code)

| Pick | Feature | Note |
|---|---|---|
| [ ] | Automates (auto-tag/move rules on upload) | Removed in 3.x, planned for later |
| [ ] | Duplicate detection / "apps" plugin system | Only documented as a concept |
| [ ] | Data retention policies, e-signatures | Mentioned as future/enterprise |

---

### Suggested MVP cut

If you just want a working first release, pick these:

1. Upload (PDF/images)
2. Versions
3. Folders with Home/Inbox
4. Tags
5. Page delete, reorder and rotate
6. OCR through a background queue
7. PostgreSQL full-text search
8. Users with JWT auth
9. Local storage
10. OpenAPI
11. The basic file browser and viewer UI

Build the rest in this order:

1. Custom fields and document types
2. Roles, scopes and sharing
3. Page move/extract (merge)
4. Audit log
5. API tokens
6. Preferences
7. Dual-panel UI
8. The extensions (OIDC, S3, path templates, remote-user auth)
