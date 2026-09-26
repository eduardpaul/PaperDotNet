# Web frontend

The web UI (Phase 8) is built on the TypeScript SDK with React, TanStack and
Tailwind ([ADR-0033](adr/0033-web-frontend.md)). This page describes:
- how it is organized;
- which screen covers which feature of the [catalog](features.md);
- the slices it is delivered in.

## UX principles

1. **Today first.** Home answers "what needs me now": tasks due, today's
   events, approvals, the Inbox and recent items.
2. **Triage, then file.** New documents land in an Inbox. Each can be
   classified, tagged, filed or turned into a task without leaving the queue.
3. **One way to work with items.** Documents, tasks, events, notes and list
   items share the same list views, item panel, fields, tags, comments,
   versions and links. Specialized screens (calendar, board, document viewer,
   note editor) are layouts of that model, not separate apps.
4. **Keyboard first, mouse friendly.** The command palette (⌘K / Ctrl+K) jumps
   anywhere, runs actions and searches. Lists support arrow keys, `Enter` to
   open, `x` to select and `e` to edit.
5. **Live and honest.** Processing, notifications and other people's changes
   appear live (SSE). Saves use ETags, and a conflict shows what changed.
6. **Everything has a URL.** Filters, sort, view, tab and the open item are in
   the route, so any screen can be bookmarked and shared.
7. **Quiet, dense, readable.** The design is neutral with one accent color and
   comfortable density in tables. It has a dark mode, follows the user's
   preferences for dates, numbers, time zone and theme, and works from phone
   width upwards.
8. **Actions only where allowed.** Buttons follow the user's scopes and access
   level. The server stays the authority.

## Information architecture

```
┌ Sidebar ─────────────┐┌ Top bar: breadcrumbs · search (⌘K) · upload · notifications · account ┐
│ Home                 ││                                                                          │
│ Inbox           (3)  ││  Page                                                                    │
│ My tasks             ││   ├ list/table/board/calendar/gallery view                               │
│ Calendar             ││   └ item panel (side sheet, full page on phones):                        │
│ Approvals       (1)  ││       Details · Preview · Comments & activity · Versions · Links         │
│ Search               ││       · Tasks · Permissions                                              │
│ ── Smart folders ──  ││                                                                          │
│ ── Workspaces ──     ││                                                                          │
│   ▸ Finance          ││                                                                          │
│     Documents, …     ││                                                                          │
│ Settings · Admin     ││                                                                          │
└──────────────────────┘└──────────────────────────────────────────────────────────────────────────┘
```

## Screens and features

| Screen | Route | Features |
|---|---|---|
| Sign in (password, passkey), callback, sign-out | `/login`, `/callback` | IAM-01, IAM-02, IAM-15 (proxy sign-in is transparent) |
| Home | `/` | LST-07, TSK-03, CAL-03, NTF-01, EVT-08 (approvals) |
| Inbox (personal and group inboxes) | `/inbox`, `/inbox/$groupId` | LST-07, DOC-01, DOC-09, DOC-10, DOC-16, AI-02/03 suggestions |
| Workspaces, workspace overview, members | `/w`, `/w/$workspaceId` | PLT-07, IAM-07 |
| List or library view (table, board, calendar, gallery; folders; filters; bulk edit; upload) | `/w/$workspaceId/l/$listId` | LST-01, LST-04…06, LST-09, LST-10, DOC-01, TSK-04 |
| Item panel: details and field editors | `…?item=$itemId` | LST-03, LST-04, LST-15, LST-19, TAX-04, TAX-06 |
| Item panel: preview and page tools | `…&tab=preview` | DOC-04, DOC-05, DOC-06, DOC-07, DOC-08, DOC-09, DOC-17 |
| Item panel: versions and file versions | `…&tab=versions` | LST-12, DOC-03 |
| Item panel: comments and activity | `…&tab=activity` | LST-17 |
| Item panel: links, backlinks, note links, tasks | `…&tab=links` | LST-08, TSK-02, TSK-06 |
| Item panel: permissions, follow | `…&tab=access` | IAM-07, NTF-03 |
| Note editor (Markdown with live preview and `[[links]]`) | `…?item=$itemId` for notes | LST-18 |
| Recycle bin | `/w/$workspaceId/l/$listId/recycle-bin` | LST-13 |
| My tasks (today, upcoming, overdue; board) | `/tasks` | TSK-01…05 |
| Calendar (month, week, agenda; recurrence editor; .ics import; feeds) | `/calendar` | CAL-01…04 |
| Approvals | `/approvals` | EVT-08 |
| Search (facets, modes, page hits) | `/search?q=` | SRC-01…03, SRC-07…09 |
| Smart folders (rules editor, grouped navigation, drop to classify) | `/f/$folderId` | TAX-08…10 |
| Notifications | `/notifications` | NTF-01, NTF-02 |
| Settings: profile, password, passkeys, preferences | `/settings` | IAM-01, IAM-14, PLT-17 |
| Settings: API tokens, calendar feeds, notification channels, subscriptions | `/settings/*` | IAM-03, CAL-04, NTF-03…05, API-06 |
| Workspace settings: lists, members, permissions, automations, runs | `/w/$workspaceId/settings/*` | PLT-07, LST-16, IAM-07, EVT-07, EVT-08 |
| List settings: fields, content types, views, versioning, documents, permissions | `/w/$workspaceId/l/$listId/settings/*` | LST-02, LST-03, LST-09, LST-11, DOC-07, DOC-10, DOC-14, IAM-07 |
| Admin: users, groups, roles | `/admin/people` | IAM-05, IAM-06, IAM-14 |
| Admin: term store, keyword promotion, CSV import | `/admin/terms` | TAX-01…03, TAX-05, TAX-11 |
| Admin: organization defaults, extensions, applications | `/admin/*` | PLT-18, EXT-03, IAM-02 |
| Admin: audit log, reindex, provisioning, export/import | `/admin/*` | LST-14, SRC-10, PRV-01…02, PLT-13, PLT-15 |

Known API gaps (the UI works around them, to be closed in the API):
- **Moving a document to another library** copies its pages (extract) and deletes the original, so fields other than the
  title and older versions stay behind. A move endpoint that keeps them is planned.

Not in the web UI: operator work done with the CLI or configuration (backup,
tenants, quotas), and protocol clients (MCP, WebDAV, CalDAV, the client CLI).

## Code organization (`web/`)

```
web/
  src/
    main.tsx            app bootstrap (query client, router, theme)
    api/                SDK client + session, query keys, hooks per resource
    routes/             TanStack Router file routes (typed params and search)
    components/ui/      primitives (Radix + Tailwind): button, dialog, sheet, menu, …
    components/         shared app components (shell, command palette, field editors, …)
    features/<area>/    screens and hooks per area (documents, tasks, calendar, …)
    extensibility/      registries: navigation, item panels, field editors
    lib/                formatting (Intl + preferences), utilities
  e2e/                  Playwright tests against a real host
```

Rules:
- API calls go through the SDK, with query keys from `api/keys.ts`. Mutations
  invalidate by key, and live events invalidate what they touch.
- Dates, numbers and time zones are formatted with `lib/format.ts`, which
  follows the user's preferences. Never use `toLocaleString` directly.
- A field type has one editor (`features/fields/editors.tsx`) and one display (`display.tsx`). Names of
  ids in values (people, terms, looked-up items) are resolved once per page by `ValueNamesProvider`.
- Tabs of the item panel register in `extensibility/item-panels.tsx`.

## Development

```bash
npm install                                  # at the repository root (workspaces: sdk/typescript, web)
dotnet run --project src/PaperDotNet.Host    # API on http://localhost:5080 (Development settings)
npm run dev -w web                           # UI on http://localhost:5173, proxied to the API
npm run check -w web                         # typecheck, lint, format check, unit tests
npm run test:e2e -w web                      # builds and starts the host, runs Playwright
```

The end-to-end tests start the host with the built UI on port 5199 (`PAPERDOTNET_E2E_PORT`) and a
temporary SQLite database; `PAPERDOTNET_WEB_URL` runs them against a running server instead.
`PLAYWRIGHT_CHROMIUM_PATH` uses an installed Chromium instead of Playwright's own. The same tests
run on PostgreSQL with the variables shown in [sdk/README.md](../sdk/README.md#end-to-end-tests).

## Slices

| Slice | Content | Status |
|---|---|---|
| **8a Foundation** | Stack and tooling; host serves the UI; sign-in (password, passkey) and sign-out; shell with sidebar, workspaces, command palette, theme, notifications bell and live events; Home; end-to-end harness | ✅ |
| **8b Lists and items** | New lists from templates; list page with saved views (table with server-side sort, board with drag and drop and a keyboard "Move to" menu), title search, folders with breadcrumbs, selection with bulk edit and delete, paging; item panel (`?item=`) with an editor per field type (text, note, email, URL, number, currency, boolean, date, date-time in the user's time zone, choice, people, lookup, managed metadata, keywords, JSON for extension types), create and edit with merge patches and If-Match (conflicts: reload or save anyway), validation next to the field, delete; version history with restore; recycle bin; lists in the sidebar and the command palette; Home, agenda, approvals and notifications open their items | ✅ |
| **8c Documents** | Uploads by button or drag and drop into libraries (current folder), the Inbox and group inboxes, with a tray (state, duplicate warnings, links); libraries as a table with thumbnails or a thumbnail grid; Inbox page (personal and group inboxes, count on Home); Preview tab: live processing status, download, replace file, run OCR again, page images, rotate, delete, reorder and split off pages (saved as a new version), file versions with restore; "Move to a library" | ✅ |
| **8d Search and navigation** | Search page (best/exact words/meaning, facets with names, highlighted snippets, page hits that open the page, paging); search in the command palette; smart folders in the sidebar (rules: kinds, tags with any/all, condition, up to 3 grouping levels), folder page with sub-folders and "take out", "Add to smart folder" and drag and drop of list rows onto a folder (drop to classify); OCR languages per file; open forms follow changes made elsewhere | ✅ |
| 8e Tasks, calendar, approvals | My tasks, board, checklist, subtasks and dependencies, recurrence; calendar month, week and agenda, event editor with RRULE, .ics import, feeds; approvals | planned |
| 8f Collaboration and notes | Comments with @mentions, activity, links and backlinks, follow, notifications page, Markdown notes with `[[links]]` | planned |
| 8g Settings | Profile, password, passkeys, preferences, API tokens, notification channels, calendar feeds, subscriptions | planned |
| 8h Administration | Workspace and list settings (fields, content types, views, permissions, automations), users, groups, roles, term store, organization defaults, extensions, applications, audit log, reindex, provisioning, export and import | planned |
