# Item and permission storage: options

**Status:** decided in [ADR-0035](adr/0035-item-storage-and-permissions-at-scale.md)
(2026-09-28). This document keeps the options and measurements behind it; where
they differ, the ADR wins (it extends the reference table of decision 2 to
multi-select choices and allows 10 promoted fields of each type).

**Inputs:** issues [0001](../issues/0001-list-pages-slow-as-a-folder-grows.md)–[0012](../issues/0012-permission-change-forces-full-delta-resync.md),
and the benchmark in [`tests/benchmarks/item-storage`](../tests/benchmarks/item-storage/README.md).
Every number below comes from that benchmark and can be run again. The
permission queries were also checked through the real `ListsDbContext` with
EF Core 10 ([EF check](#checked-through-ef-core-10)).

## Summary

ADR-0011's scope model is sound. Each item stores the id of the nearest object
with unique permissions (SharePoint's `ScopeId`), and a query filters on it.
What does not scale is how the allowed scopes are computed: **every request
loads every unique scope of the list.** The benchmark library is laid out like
a Papermerge import (1M documents, 7,500 unique scopes). There, that load is a
full scan of `items` on every request: 84–94 ms on PostgreSQL and
260–265 ms on SQLite. It caps the list page at about 18 requests/s
on 4 cores. The other two limits are sorting or
filtering on JSON fields, and rebuilding whole lists after a permission
change.

| Decision | Recommended | Good alternative |
|---|---|---|
| [1. Permissions](#decision-1-permission-model) | **A. Scope ACL, looked up by principal** | **C. Access expanded per user**, if nested groups or role hierarchies become a requirement |
| [2. Fields](#decision-2-field-storage) | **H. Typed slot columns for promoted fields, plus a reference table for multi-valued people, lookups and terms**. JSON stays the source | **S. Slot columns only** (no index for multi-valued fields) |
| [3. Derived data](#decision-3-search-delta-live-events-and-fan-out) | **Scope id on search documents, delta and live events**; subtree rewrites as chunked background work | **Per-document principals as today**, rebuilt only for the affected items |

What the recommendation changes, measured on the same data and machine:

| | Today | Recommended |
|---|---:|---:|
| List page request (access + page), PostgreSQL, 1 / 4 / 16 clients | 12 / 16 / 18 req/s | 1,114 / 5,891 / 4,858 req/s |
| Access check before the page, PostgreSQL / SQLite | 96 ms / 271 ms | 0.13 ms / 0.08 ms |
| CRM view, 500k deals: one stage, by close date, PostgreSQL / SQLite | 160 ms / 475 ms | 1.9 ms / 1.6 ms |
| My tasks over 50 task lists, PostgreSQL / SQLite | ≥ 93 ms / ≥ 135 ms | 0.79 ms / 0.37 ms |
| Deals of one account (lookup), SQLite | 474 ms | 0.11 ms |
| Raw write throughput with three promoted fields, 8 writers, PostgreSQL: update / insert | 7,254 / 9,007 tps | 5,573 / 7,260 tps (−23% / −19%) |

## 1. What was measured

The benchmark builds one tenant with 5,000 users and 50 groups, and 1.6M items:

- a **library** of 1,035,001 rows laid out like a Papermerge import (one home
  folder per user with unique permissions, group-shared folders, 2,500
  documents shared with one extra person, a public folder that inherits);
- a **CRM list** of 500,000 deals;
- **50 task lists** of 2,000 tasks.

It runs SQL shaped like the queries EF Core generates today, and like the
queries each candidate would generate. Tables mirror the EF model. PostgreSQL 16 (1 GB shared buffers) and
SQLite 3.45 (WAL, the app's pragmas) ran on the same 4-core VM with warm
caches. `u1` reads 3 scopes, `u5` reads 202 (about 50,000 documents). The
application, EF Core and the network are not included; the
[EF check](#checked-through-ef-core-10) adds EF Core for the permission
queries.

What today's design costs:

| Finding | PostgreSQL | SQLite | Issue |
|---|---:|---:|---|
| Loading every unique scope of the list (no index can serve `HasUniquePermissions`) | 84–94 ms, parallel scan of all of `items` | 260–265 ms | [0003](../issues/0003-permission-scope-preload-grows-with-list-size.md) |
| The same with a partial index (minimal fix): still grows with unique scopes | 5.1 ms | – | 0003 |
| List page request, 16 clients | 18 req/s, 904 ms average | – | [0002](../issues/0002-uncached-permission-lookups-on-every-request.md) |
| Stage filter + sort by date on a JSON field, 500k rows | 160 ms | 475 ms (`json_extract`), 2.00 s through a per-row JSON function like `pdn_json_contains` | [0005](../issues/0005-dynamic-json-fields-no-promotion-path-for-hot-columns.md), [0010](../issues/0010-sqlite-field-filters-parse-json-per-row.md) |
| Board counts per stage, 500k rows | 299 ms | 770 ms | 0005 |
| My tasks over 50 lists, one list at a time (only the access check and the query of each list, so a lower bound) | 93 ms | 135 ms | [0009](../issues/0009-cross-list-queries-load-access-per-list.md) |
| Rewriting the scope of 100,000 items (break inheritance, move) in one statement | 4.23 s, row locks on the subtree | 1.53 s, database-wide write lock; an edit in another list waited 1.53 s | [0004](../issues/0004-permission-change-fanout-is-a-large-inline-transaction.md) |

Found by reading the code (not in the benchmark):

- Any permission change, folder move or restore rebuilds the list's whole
  search index. The rebuild deletes the passages, so every document of the list
  is embedded again ([0008](../issues/0008-permission-change-rebuilds-list-search-index.md)).
- Any permission change makes every delta client resync the whole list
  ([0012](../issues/0012-permission-change-forces-full-delta-resync.md)).
- `item.changed` live events go to every user of the tenant
  ([0011](../issues/0011-item-live-events-reach-every-tenant-user.md)).
- On PostgreSQL, the tenant `set_config` runs each time EF opens a connection.
  Outside a transaction EF opens one per query, so it is an extra round trip
  per query, not per physical connection. Confirmed in the
  [EF check](#checked-through-ef-core-10): five queries, five opens, five
  `set_config` in the PostgreSQL log
  ([0007](../issues/0007-postgresql-rls-session-overhead-under-pooling.md);
  fixed by ADR-0035 step 6: the setting now travels with each command).

## 2. What the design must handle

| Scenario | Access pattern | Hot queries |
|---|---|---|
| **DMS** (Papermerge-like) | Home folders with unique permissions, group-shared folders, most items inherit | Browse a folder (folders first, by title), search, recent documents |
| **DMS with sharing** (IAM-08…11) | Many individually shared folders and documents, guests, links | "Shared with me", the same browsing for guests |
| **CRM** | Flat lists of 100k–1M records, access per list or folder | Views filtered and sorted by fields (stage, amount, close date), board counts, reverse lookups (deals of an account), many small concurrent edits |
| **Tasks and calendar** | Many lists, mostly inherited access | "Mine" across all lists, date ranges |
| **Search** | Trimmed by the same rules | Keyword, semantic and hybrid over everything readable |

Constraints: SQLite and PostgreSQL both (ADR-0009); EF Core only, no raw SQL
outside the provider projects; the tenant filter and RLS stay; one container;
no schema changes at runtime.

## Decision 1: permission model

### The options

- **P0. Keep ADR-0011, patch it.** Add a partial index for the unique-scope
  query and cache the list's scopes per user.
- **A. Scope ACL, looked up by principal (recommended).** Every item has a
  scope: the list id when it inherits from the list. `acl_entries(scope,
  principal, level)` is indexed by principal. Workspace roles are
  pseudo-principals (members, visitors, owners of workspace W), so membership
  changes write nothing. The caller's principal set (user, groups, workspace
  roles) is cached. The allowed scopes come from one index-only query on the
  principals; it never enumerates the list.
- **C. Access expanded per user.** A table `user_access(user, scope, level)`
  resolves groups and workspace roles ahead of time and is refreshed when
  memberships or grants change. Prior art: GitLab's `project_authorizations`,
  Salesforce share tables.
- **R. Compute on read.** No materialized scope: a closure table of ancestors
  (or a Zanzibar-style engine such as OpenFGA or SpiceDB) decides access by
  walking the tree at query time.

### Versus

| | P0 patch | **A. ACL by principal** | C. Per-user access | R. Compute on read |
|---|---|---|---|---|
| Access check per request | Grows with the unique scopes of the list | Grows with the caller's grants only | Grows with the caller's grants only | Walks ancestors per item |
| Grant change | Few rows | Few rows | Rows × members of the group | One row |
| Group or workspace membership change | Nothing | Nothing | Rows × scopes granted to the group or workspace | Nothing |
| Break or reset inheritance, cross-scope move | Rewrites the subtree's scope | Rewrites the subtree's scope | Rewrites the subtree's scope | One row |
| Queries across lists (My tasks, search, smart folders) | One access load per list | One lookup for the tenant | One lookup for the tenant | A list of all readable objects is needed first (becomes C) |
| Storage (benchmark tenant) | 11,052 grants | 11,052 entries (2.6 MB) | 449,000 rows (75 MB) | Closure rows ≈ items × depth |
| Consistency | Immediate | Immediate | Refresh after each change (sync or background) | Immediate |
| Nested groups, role hierarchies | No | No (flat groups) | Yes | Yes |
| Fits one container, SQLite and PostgreSQL | Yes | Yes | Yes | Closure: yes. Engine: an extra service, no SQLite |
| Change from today | Small | Medium | Large | Large |

### Measured

| | P0 patch | A | C |
|---|---:|---:|---:|
| Access check, PostgreSQL (u1 / u5) | 5.1 ms + 1.3 ms | 0.04 ms / 0.13 ms | 0.05 ms / 0.85 ms |
| Access check, SQLite (u1 / u5) | – | 0.01 ms / 0.08 ms | – |
| List page request, PostgreSQL, 1 / 4 / 16 clients | 138 / 552 / 506 req/s | 1,114 / 5,891 / 4,858 req/s | as A |

After the access check, A and C run the same page queries. Page queries
compared with today, PostgreSQL / SQLite:

| Query (u1 / u5) | Today | A |
|---|---|---|
| Own sub-folder, folders first by title | 0.14 ms / 0.13 ms · 0.13 ms / 0.23 ms | 0.06 ms / 0.10 ms · 0.15 ms / 0.20 ms |
| List root (5,001 folders, a few visible) | 6.1 ms / 6.4 ms · 1.1 ms / 1.4 ms | 2.4 ms / 6.4 ms · 2.2 ms / 1.3 ms |
| All readable documents by id (first page) | 31 ms / 9.3 ms · 8.5 ms / 43 ms | 36 ms / 9.7 ms · 0.35 ms / 2.7 ms |
| Count readable documents | 6.3 ms / 17 ms · 6.1 ms / 271 ms | 18 ms / 92 ms · 8.1 ms / 37 ms |

Reading the table:

- On PostgreSQL the root listing is faster with A. Every item has a scope, so
  there is no `ScopeId IS NULL OR …`, and PostgreSQL combines the scope and
  parent indexes. On SQLite it is the same as today.
- On SQLite, "everything by id" is faster with A (subquery shape).
- Counts grow with the rows the user can see, in every design, and the plan
  decides the rest. Counting u5's 50,000 documents took 17 ms today
  and 92 ms with A on PostgreSQL, and 271 ms and
  37 ms on SQLite. Count once, not on every page
  ([0001](../issues/0001-list-pages-slow-as-a-folder-grows.md)).
- **SQLite needs the allowed scopes as one parameter.** With a plain
  `IN (…)` list of 203 values, SQLite chose the scope index for "everything
  by id" and took 502 ms, compared with 2.7 ms with a subquery or
  `json_each`. EF Core 10 sends exactly that list by default on SQLite, one
  parameter per value. `EF.Parameter(allowed)` fixes it with one LINQ shape
  for both providers (see the [EF check](#checked-through-ef-core-10)).

### Checked through EF Core 10

The benchmark runs hand-written SQL. To check what the application would
actually send, [`tests/benchmarks/item-storage/ef`](../tests/benchmarks/item-storage/ef/run-ef.sh)
runs the permission queries through the real `ListsDbContext`, registered as
the host does (`AddPaperDotNetSqlite` / `AddPaperDotNetPostgreSql`,
`AddModuleDbContext`, with the tenant filter and the RLS interceptor), on the
same 1.6M rows in EF's own schema. EF Core 10.0.12, Npgsql 10.0.3, user `u5`
(202 allowed scopes), median of 5:

| EF query | SQLite | PostgreSQL |
|---|---:|---:|
| Today: all unique scope ids of the list (`ListSchemaLoader`) | 386 ms | 81 ms |
| Today: all readable documents by id, `allowed.Contains` | 304 ms | 4.3 ms |
| A: allowed scopes from the ACL by principal | 1.0 ms | 2.1 ms |
| A: all readable documents by id, `allowed.Contains` | **639 ms** | 7.1 ms |
| A: the same with a subquery on the ACL | 4.9 ms | 12 ms |
| A: the same with `EF.Parameter(allowed).Contains` | **5.8 ms** | **6.6 ms** |
| A: folder page with `EF.Parameter(allowed).Contains` | 1.7 ms | 2.5 ms |

What it showed:

- **EF Core 10 translates `allowed.Contains(x)` differently per provider.**
  PostgreSQL gets one array parameter (`= ANY (@allowed)`). SQLite gets one
  parameter per value (`IN (@allowed1, @allowed2, …)`), and SQLite then
  picks the bad plan, today as well (304 ms). `EF.Parameter(allowed)` makes
  SQLite use one JSON parameter with `json_each`, and PostgreSQL keeps its
  array. That is one LINQ shape for both, with no provider-specific code. The
  global switch (`UseParameterizedCollectionMode`) would change every query
  of the context, so the per-query form is safer.
- **Nullable `ScopeId`:** EF adds an `array_position(@allowed, NULL)` term on
  PostgreSQL. Make the property non-nullable.
- **Today's cost is real in EF too:** loading every unique scope took 386 ms
  (SQLite) and 81 ms (PostgreSQL) per request.
- **One connection open per query:** five queries outside a transaction
  opened five connections on both providers. On PostgreSQL, the server log
  showed five `set_config('app.tenant_id', …)`. In one transaction: one
  open. On SQLite, every open also runs the connection pragmas and registers
  `pdn_json_contains` again.

### Why A

- It removes the cost that grows with the list. The access check is an
  index-only lookup (0.13 ms for 202 scopes). It can be cached, but
  does not have to be.
- Grants stay small rows. Unlike C, workspace and group membership changes
  write nothing, and nothing is stale.
- One lookup serves every consumer: list pages, My tasks, calendar, smart
  folders, search, MCP, live events, delta.
- It is ADR-0011 evaluated from the principal side. The API, the grants UI and
  SharePoint semantics (break, copy, reset) stay.
- C only wins when groups nest or access follows a role hierarchy (a manager
  inherits what their reports can read). In
  that case, add a group-expansion table to A rather than expanding every
  grant.
- R makes permission writes cheap and every read expensive. Filtering, sorting
  and paging a list in SQL needs the set of readable objects up front, which
  brings back C. An external engine also breaks the one-container install.

### A in detail

- `items.ScopeId` is never null. It is the list id when the item inherits
  from the list. "Has unique permissions" becomes `ScopeId == Id`. The
  property must be `Guid`, not `Guid?`: with a nullable property EF adds
  `OR (scope_id IS NULL AND array_position(@allowed, NULL) IS NOT NULL)` to
  every PostgreSQL filter.
- `lists.acl_entries`: `ScopeId`, `PrincipalKind` (user, group, workspace
  visitors, members, owners; later guest, link, everyone), `PrincipalId`,
  `Level` stored as a number (today it is a string, so `>=` cannot be
  compared in SQL), `ListId`, `WorkspaceId`, `TenantId`. The key is
  `(ScopeId, PrincipalKind, PrincipalId)`, with an index on
  `(PrincipalId, ListId, ScopeId, Level)`.
- A list that inherits from its workspace has three entries: workspace
  visitors → Read, members → Contribute, owners → Manage. Breaking inheritance
  copies these role entries. Today it copies the members one by one, so people
  added to the workspace later get no access.
- Workspace owners keep full control: every scope of the workspace has a fixed
  owners → Manage entry, which cannot be edited. Holders of `workspace.manage`
  skip the filter.
- The caller's principal set is cached per tenant and user, invalidated by tag
  on user, group and workspace-member changes (like `EffectiveScopeProvider`).
- Allowed scopes: `SELECT ScopeId, MAX(Level) FROM acl_entries WHERE PrincipalId
  IN (@principals) [AND ListId = @list] GROUP BY ScopeId`. Point checks
  (`Level(scopeId)`) read this set. A scope that is not in it means no access,
  so the list's other scopes are never listed.
- Queries filter with `EF.Parameter(allowed).Contains(i.ScopeId)`. EF sends
  one array parameter on PostgreSQL (`scope_id = ANY (@allowed)`) and one JSON
  parameter on SQLite (`scope_id IN (SELECT … FROM json_each(@allowed))`).
  Plain `allowed.Contains(…)` is wrong on SQLite (see below).
- Other modules get this through a contract (`IItemAccess` in
  Lists.Contracts): the principal set, the allowed scopes for a list or the
  tenant, and the filter. Modules cannot join another module's tables in EF.

## Decision 2: field storage

### The options

- **F0. JSON only (today).** One JSON document per item. On PostgreSQL, a GIN
  index serves equality. Sorts and ranges read the JSON of every row, and on
  SQLite so does equality.
- **S. Typed slot columns.** A few typed columns on `items` (`Text1..n`,
  `Number1..n`, `Date1..n`), each with a partial index `(ListId, slot, Id)`. A
  promoted field is copied from the JSON into a free slot of its type. This is
  SharePoint's `AllUserData` and Salesforce's flex columns.
- **V. Typed pivot table (EAV index).** One row per promoted value:
  `(ListId, field, text/number/date/guid value, ItemId)`. This is Salesforce's
  `MT_Indexes` and SharePoint's `NameValuePair` tables.
- **H. Hybrid (recommended).** S for single-valued fields, plus a narrow
  reference table (`item_refs`) only for values that are ids: people,
  lookups, terms, multi-valued or not. SharePoint does the same with
  `AllUserData` and `AllUserDataJunctions`.
- **T. A real table per list.** Typed columns, schema changed at runtime.
  Baserow, NocoDB and Twenty do this.
- **E. Expression indexes per list.** `CREATE INDEX … ((fields->>'x')) WHERE
  ListId = …`, created at runtime per promoted field.

### Versus

| | F0 JSON | S slots | V pivot | **H hybrid** | T table per list | E expression indexes |
|---|---|---|---|---|---|---|
| Sort, range, equality on a promoted field | Scan | Index | Index for one field; several fields join badly | Index | Index | Index |
| "Contains me", tags, reverse lookup | GIN on PostgreSQL, scan on SQLite | No | Index | Index | Needs its own table | No |
| Same field across lists (My tasks) | Scan per list | Index when the slot is fixed per content type | Index | Index | One table per list | No |
| Raw writes, 8 writers, PostgreSQL (update / insert tps) | 7,254 / 9,007 | 5,573 / 7,260 (−23% / −19%) | 3,865 / 4,088 (−47% / −55%) | S, plus one row per id value | Best | Grows with every index on the shared table |
| Extra storage (600k items) | – | Small | 658 MB (2.8M rows) | Only the references | – | Per index |
| EF model, both providers, no DDL at runtime | Yes | Yes | Yes | Yes | No | No |
| Limit | – | Slots per type per list | – | Slots per type per list | – | – |

### Measured

| Query | F0 JSON (PostgreSQL / SQLite) | S slots | V pivot | H |
|---|---|---|---|---|
| C1 stage = Negotiation, by close date, first 100 of 500k | 160 ms / 475 ms | 1.9 ms / 1.6 ms | 148 ms (hash joins over 83k and 500k values) | as S |
| C2 amount range and close-date range, count | 101 ms | 4.6 ms | 16 ms | as S |
| C3 board: count per stage | 299 ms / 770 ms | 55 ms / 44 ms | 100 ms | as S |
| C4 deals of one account | 0.14 ms (GIN) / 474 ms | – | 0.24 ms / 0.11 ms | as V |
| T1 my open tasks across 50 lists by due date | 93 ms / 135 ms (one list at a time); 7.3 ms as one JSON query on PostgreSQL | – | – | 0.79 ms / 0.37 ms |

### Why H

- On promoted fields the CRM view (C1) ran 85 times (PostgreSQL) and
  302 times (SQLite) faster than on JSON, the range count (C2)
  22 times, and the board counts (C3) 5 and 17
  times.
- V fails exactly where CRM views live: a filter on one field sorted by
  another. The planner hash-joins the value sets instead of walking one index,
  and V costs the most on writes. H uses rows only for id values, where a
  lookup by value is the whole query.
- Promoted fields are not free. With three slots indexed and filled on every
  row, raw updates ran −23% and inserts −19%
  compared with JSON only (median of three interleaved rounds). In the
  application each write also writes a version, the change log, the audit
  log and the outbox, so the share is smaller, and the absolute rates are far
  above what the API serves today. Promote only fields that are sorted,
  filtered or grouped.
- It is the SharePoint layout, which suits a SharePoint-like product. T and E
  need schema changes at runtime in a shared, multi-tenant, EF-mapped table on
  two databases. That is the opposite of a simple self-hosted install.

### H in detail

- `Fields` (JSON) stays the source of truth for validation, versions and the
  API. Slots and references are derived and written in the same transaction
  by `ItemWriter` (only changed references are rewritten).
- A field definition gets `indexed: true`. It is on by default for built-in
  content types: task status, due date and priority; event start and end;
  assignees; lookups. Custom fields are promoted by the owner.
- Slot budget, for example 4 text, 2 number and 4 date per list. SharePoint
  allows 20 indexed columns per list. Built-in content types always use the
  same slots, so a query across lists (My tasks) reads one column.
- `item_refs(ItemId, FieldKey, Value, ListId, TenantId)`, indexed on
  `(FieldKey, Value, ListId, ItemId)`. `any()` and `eq` on such fields become
  `EXISTS`. Tag filters in list views use it; search keeps `document_tags`.
- `ItemQueryTranslator` maps a promoted field to its column or to
  `item_refs`. Other fields stay on JSON, which is fine for small lists.
- Promoting an existing field is a background backfill (`IOperations`). The
  translator uses JSON until the backfill is done.
- Also from [0001](../issues/0001-list-pages-slow-as-a-folder-grows.md): a
  browse index `(ListId, ParentId, IsFolder, Title, Id)`, keyset paging on it,
  count once, and a case-insensitive title index for the search box.

## Decision 3: search, delta, live events and fan-out

### Search trimming

| | D1. Per-document principals (today), rebuilt for affected items only | **D2. Scope id on the document, allowed scopes at query time** | D3. Scope id on the document + scope → principals table in Search |
|---|---|---|---|
| Grant change | Rewrites principal rows for every item under the scope | Nothing | Rewrites the scope's rows |
| Break, reset, move | Same as a grant change | Updates one column of the moved documents | Updates one column of the moved documents |
| Query | `EXISTS` on principals (today) | `ScopeId IN (allowed)` (the tenant's allowed set, cached) | `EXISTS` on scope principals |
| External engines (idea 0023) | Reindex on every grant change | Filter on a `scopeId` attribute; grants never reindex | Needs a join, which engines lack |
| Owns the rule | Lists and Search each | Lists only (`IItemAccess`) | Lists and Search each |

**D2 is recommended.** A heavy user's allowed set for the whole tenant can
reach thousands of scopes. That is still a small array for PostgreSQL and
`json_each` for SQLite, and it is cached per user.

### Fan-out rules (any option)

- **Grant change:** writes the scope's entries only, evicts caches, and
  publishes `ScopeAccessChanged(scopeId)`. No item or search rows change.
- **Break inheritance (copy):** first write the new scope's entries as a copy
  of the parent's. Then move the subtree to the new scope in chunks of about
  2,000–5,000 rows, each in its own transaction, as an `IOperations` job
  (202). Until an item is moved, its access is identical, so there is no gap.
- **Reset inheritance:** first overwrite the scope's entries with a copy of
  the parent's. Then move the subtree back in chunks, then delete the entries.
  Access is correct at every step.
- **Moving an inheriting folder across scopes:** reassign inline up to a
  threshold (for example 5,000 items), otherwise in the background. Until
  that finishes, moved items keep their old access for a short time; this has
  to be documented or accepted (open question 2).
- **Why chunks:** 100,000 rows in one statement held the SQLite write lock for
  1.53 s (1.4–1.5 s in two more runs), blocking every writer in the
  database. Roughly 300,000 rows would pass the 5 s `busy_timeout`, and other
  writes would start failing. 10,000 rows took 0.4–1.2 s on SQLite across
  three runs (pages not yet in the cache are read while the lock is held) and
  644 ms on PostgreSQL.
- **Delta:** log `ScopeChanged(scopeId)` instead of a list-wide reset. The
  next delta call returns that scope's items as changed or removed, depending
  on the caller's access, with 410 only above a size limit ([0012](../issues/0012-permission-change-forces-full-delta-resync.md)).
- **Live events:** deliver `item.changed` only to connections whose principal
  set matches the scope's entries ([0011](../issues/0011-item-live-events-reach-every-tenant-user.md)).

## Concurrency notes

PostgreSQL:

- With A, the access check is an index-only read, so reads scale with cores:
  5,891 requests/s with 4 clients on 4 cores, compared with 16
  today.
- Item writes touch only their own rows: no counters on folders or lists
  (checked in `ItemWriter`). Keep it that way; count once instead of storing
  counts ([0001](../issues/0001-list-pages-slow-as-a-folder-grows.md)).
  Optimistic concurrency with `Version` holds no locks between requests.
- Keep write transactions short: fan-out in chunks; search and embeddings
  stay behind the outbox.
- Every index on `items` is paid on every update that changes an indexed
  column. The GIN index on `Fields` makes every field change such an update.
  Keep the set small: primary key, browse, `(ScopeId, Id)`, the partial slot
  indexes, and GIN on `Fields`. Once
  hot fields are promoted, check whether GIN still earns its write cost.
- Send the tenant setting once per request or connection, not before each
  query. Under a transaction-mode pooler it must be in the same transaction as
  the query ([0007](../issues/0007-postgresql-rls-session-overhead-under-pooling.md)).
- Later, for very large installs: partition `items` by hash of tenant or list.
  That is DDL in the PostgreSQL migrations only; EF does not see it.

SQLite:

- There is one writer. Long write transactions are the main risk, hence the
  chunked fan-out in small chunks (10,000 rows held the lock for up to
  1.2 s when its pages were not cached).
- No JSON parsing per row in filters: promote the fields
  ([0010](../issues/0010-sqlite-field-filters-parse-json-per-row.md)).
- Access filter as one JSON parameter (`EF.Parameter`), not one parameter
  per scope (decision 1).

## Target model (sketch)

```
lists.items
  Id, TenantId, ListId, ContentTypeId, ParentId, IsFolder, Title
  Fields                 JSON, the source of truth
  ScopeId    not null    list id, or the nearest folder/item with unique permissions
  Text1..4, Number1..2, Date1..4        promoted single-valued fields
  CreatedAt/By, UpdatedAt/By, DeletedAt/By, Version
lists.item_refs     (ItemId, FieldKey, Value) + ListId, TenantId          ids in person, lookup and term fields
lists.acl_entries   (ScopeId, PrincipalKind, PrincipalId) + Level, ListId, WorkspaceId, TenantId
search.documents    + ScopeId;  document_principals removed
```

There are no users yet, so the API can change where that helps:

- permission responses show role principals ("members of the workspace")
  instead of expanded members;
- field definitions get `indexed`;
- break, reset and large moves may answer 202 with an operation;
- migrations are generated again for both providers (no data to migrate).

## Order of work

1. A: principal-set cache, non-null scope, `acl_entries`, `IItemAccess`,
   the `EF.Parameter` filter shape, tenant-isolation and permission tests, and
   a permission-heavy scenario in `PaperDotNet.Performance` (0002, 0003, 0006).
2. Fan-out: chunked background moves, search by scope, delta scope markers,
   live-event audience (0004, 0008, 0011, 0012).
3. H: slots, `item_refs`, translator mapping, promotion backfill; built-in
   content types promoted (0001, 0005, 0010).
4. Queries across lists as one query: My tasks, calendar, smart folders (0009).

## Decisions taken

The open questions were answered on 2026-09-28 and are recorded in
[ADR-0035](adr/0035-item-storage-and-permissions-at-scale.md):

1. **Indexed fields per list:** configurable per type, starting at 10 text,
   10 number and 10 date. Multi-select choices and multi-lookups were reviewed
   and go to one value table with the other multi-value fields. Measured
   again for this: 30 slots in the schema cost about 28% of raw write
   throughput when rows fill 3 of them and 54–69% when they fill all 30. The
   value table makes "one of", "all of", "none of", counts per value and
   reverse lookups index lookups, at about 240 bytes per value.
2. **Large moves:** allow the short window; up to 5,000 items are reassigned
   in the request, larger subtrees in the background.
3. **Groups:** groups can contain groups (a group closure table in Identity).
4. **Sharing (IAM-08):** break and copy.
5. **GIN on PostgreSQL:** kept.
