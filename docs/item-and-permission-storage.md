# Item and permission storage: options

**Status:** proposal, waiting for a decision (2026-09-28). The options chosen
here become ADR-0035. It would replace how ADR-0011 evaluates permissions (the
scope model stays) and how ADR-0012 trims search.

**Inputs:** issues [0001](../issues/0001-list-pages-slow-as-a-folder-grows.md)–[0012](../issues/0012-permission-change-forces-full-delta-resync.md),
and the benchmark in [`tests/benchmarks/item-storage`](../tests/benchmarks/item-storage/README.md).
Every number below comes from that benchmark and can be run again.

## Summary

ADR-0011's scope model is sound. Each item stores the id of the nearest object
with unique permissions (SharePoint's `ScopeId`), and a query filters on it.
What does not scale is how the allowed scopes are computed: **every request
loads every unique scope of the list.** The benchmark library is laid out like
a Papermerge import (1M documents, 7,500 unique scopes). There, that load is a
full scan of `items` on every request: 94–121 ms on PostgreSQL and
312–317 ms on SQLite. It caps the list page at about 12 requests/s
on 4 cores. The other two limits are sorting or
filtering on JSON fields, and rebuilding whole lists after a permission
change.

| Decision | Recommended | Good alternative |
|---|---|---|
| [1. Permissions](#decision-1-permission-model) | **A. Scope ACL, looked up by principal**, plus **ownership rules** for private records (CRM) | **C. Access expanded per user**, if nested groups or manager hierarchies become a requirement |
| [2. Fields](#decision-2-field-storage) | **H. Typed slot columns for promoted fields, plus a reference table for multi-valued people, lookups and terms**. JSON stays the source | **S. Slot columns only** (no index for multi-valued fields) |
| [3. Derived data](#decision-3-search-delta-live-events-and-fan-out) | **Scope id on search documents, delta and live events**; subtree rewrites as chunked background work | **Per-document principals as today**, rebuilt only for the affected items |

What the recommendation changes, measured on the same data and machine:

| | Today | Recommended |
|---|---:|---:|
| List page request (access + page), PostgreSQL, 1 / 4 / 16 clients | 9 / 13 / 12 req/s | 1,030 / 5,289 / 4,074 req/s |
| Access check before the page, PostgreSQL / SQLite | 122 ms / 321 ms | 0.16 ms / 0.07 ms |
| CRM view, 500k deals: one stage, by close date, PostgreSQL / SQLite | 243 ms / 480 ms | 2.9 ms / 1.5 ms |
| My tasks over 50 task lists, PostgreSQL / SQLite | ≥ 130 ms / ≥ 152 ms | 1.5 ms / 0.53 ms |
| Deals of one account (lookup), SQLite | 480 ms | 0.12 ms |
| Raw write throughput with four promoted fields, 8 writers, PostgreSQL: update / insert | 6,276 / 7,895 tps | 4,367 / 5,162 tps (−30% / −35%) |

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
application, EF Core and the network are not included.

What today's design costs:

| Finding | PostgreSQL | SQLite | Issue |
|---|---:|---:|---|
| Loading every unique scope of the list (no index can serve `HasUniquePermissions`) | 94–121 ms, parallel scan of all of `items` | 312–317 ms | [0003](../issues/0003-permission-scope-preload-grows-with-list-size.md) |
| The same with a partial index (minimal fix): still grows with unique scopes | 6.2 ms | – | 0003 |
| List page request, 16 clients | 12 req/s, 1.29 s average | – | [0002](../issues/0002-uncached-permission-lookups-on-every-request.md) |
| Stage filter + sort by date on a JSON field, 500k rows | 243 ms | 480 ms (`json_extract`), 2.08 s through a per-row JSON function like `pdn_json_contains` | [0005](../issues/0005-dynamic-json-fields-no-promotion-path-for-hot-columns.md), [0010](../issues/0010-sqlite-field-filters-parse-json-per-row.md) |
| Board counts per stage, 500k rows | 307 ms | 819 ms | 0005 |
| My tasks over 50 lists, one list at a time (only the access check and the query of each list, so a lower bound) | 130 ms | 152 ms | [0009](../issues/0009-cross-list-queries-load-access-per-list.md) |
| Rewriting the scope of 100,000 items (break inheritance, move) in one statement | 5.11 s, row locks on the subtree | 2.06 s, database-wide write lock; an edit in another list waited 2.04 s | [0004](../issues/0004-permission-change-fanout-is-a-large-inline-transaction.md) |

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
  per query, not per physical connection
  ([0007](../issues/0007-postgresql-rls-session-overhead-under-pooling.md)).

## 2. What the design must handle

| Scenario | Access pattern | Hot queries |
|---|---|---|
| **DMS** (Papermerge-like) | Home folders with unique permissions, group-shared folders, most items inherit | Browse a folder (folders first, by title), search, recent documents |
| **DMS with sharing** (IAM-08…11) | Many individually shared folders and documents, guests, links | "Shared with me", the same browsing for guests |
| **CRM** | Flat lists of 100k–1M records; private by owner or team, managers see all | Views filtered and sorted by fields (stage, amount, close date), board counts, reverse lookups (deals of an account), many small concurrent edits |
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
| Nested groups, manager hierarchies | No | No (flat groups) | Yes | Yes |
| Fits one container, SQLite and PostgreSQL | Yes | Yes | Yes | Closure: yes. Engine: an extra service, no SQLite |
| Change from today | Small | Medium | Large | Large |

### Measured

| | P0 patch | A | C |
|---|---:|---:|---:|
| Access check, PostgreSQL (u1 / u5) | 6.2 ms + 1.3 ms | 0.06 ms / 0.16 ms | 0.07 ms / 0.98 ms |
| Access check, SQLite (u1 / u5) | – | 0.01 ms / 0.07 ms | – |
| List page request, PostgreSQL, 1 / 4 / 16 clients | 104 / 463 / 410 req/s | 1,030 / 5,289 / 4,074 req/s | as A |

After the access check, A and C run the same page queries. Page queries
compared with today, PostgreSQL / SQLite:

| Query (u1 / u5) | Today | A |
|---|---|---|
| Own sub-folder, folders first by title | 0.17 ms / 0.17 ms · 0.15 ms / 0.29 ms | 0.12 ms / 0.09 ms · 0.15 ms / 0.21 ms |
| List root (5,001 folders, a few visible) | 6.1 ms / 8.8 ms · 1.1 ms / 1.4 ms | 1.4 ms / 5.6 ms · 1.4 ms / 1.4 ms |
| All readable documents by id (first page) | 46 ms / 11 ms · 6.9 ms / 30 ms | 47 ms / 14 ms · 0.41 ms / 2.6 ms |
| Count readable documents | 8.4 ms / 20 ms · 4.9 ms / 264 ms | 6.7 ms / 60 ms · 6.2 ms / 31 ms |

Reading the table:

- On PostgreSQL the root listing is faster with A. Every item has a scope, so
  there is no `ScopeId IS NULL OR …`, and PostgreSQL combines the scope and
  parent indexes. On SQLite it is the same as today.
- On SQLite, "everything by id" is faster with A (subquery shape).
- Counts grow with the rows the user can see, in every design, and the plan
  decides the rest. Counting u5's 50,000 documents went from 20 to 60 ms on
  PostgreSQL and from 264 to 31 ms on SQLite. Count once, not on every page
  ([0001](../issues/0001-list-pages-slow-as-a-folder-grows.md)).
- **The query shape must differ per provider.** PostgreSQL plans well with
  the allowed scopes as an array parameter (`ScopeId = ANY(@allowed)`): it sees
  the values. SQLite plans well with a subquery on the ACL or `json_each`. With
  a plain `IN (…)` list of 203 values, SQLite chose the scope index for
  "everything by id" and took 577 ms, compared with
  2.6 ms as a subquery. This goes behind the
  provider abstraction, like the JSON functions.

### Why A

- It removes the cost that grows with the list. The access check is an
  index-only lookup (0.16 ms for 202 scopes). It can be cached, but
  does not have to be.
- Grants stay small rows. Unlike C, workspace and group membership changes
  write nothing, and nothing is stale.
- One lookup serves every consumer: list pages, My tasks, calendar, smart
  folders, search, MCP, live events, delta.
- It is ADR-0011 evaluated from the principal side. The API, the grants UI and
  SharePoint semantics (break, copy, reset) stay.
- C only wins when groups nest or managers see their reports' records. In
  that case, add a group-expansion table to A rather than expanding every
  grant.
- R makes permission writes cheap and every read expensive. Filtering, sorting
  and paging a list in SQL needs the set of readable objects up front, which
  brings back C. An external engine also breaks the one-container install.

### A in detail

- `items.ScopeId` is never null. It is the list id when the item inherits
  from the list. "Has unique permissions" becomes `ScopeId == Id`.
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
- Other modules get this through a contract (`IItemAccess` in
  Lists.Contracts): the principal set, the allowed scopes for a list or the
  tenant, and the filter. Modules cannot join another module's tables in EF.
- Ownership rules (next section) add one condition.

### Ownership rules (add-on for any option)

A CRM list is private per record: people see the deals they own, and managers
see all. With scopes alone, every record needs its own unique scope: 500,000
scopes, over a million ACL rows, and a rewrite on every change of owner.
SharePoint has a list setting for this ("Read items that were created by the
user"), and Dynamics has user-level access.

- Add a system column `OwnerId` (a user or a group; defaults to the creator,
  can be reassigned) with an index on `(ListId, OwnerId, Id)`.
- Add a list setting `itemAccess`: `all` (default) or `own`. A later value,
  `ownOrAssigned`, would also match a person field. It applies to callers
  below Manage on the item's scope.
- The filter becomes `ScopeId IN @manage OR (ScopeId IN @read AND OwnerId IN
  @principals)`. Team ownership is a group as owner.
- Measured: "my deals" newest first in 500,000 rows took 0.30 ms on
  PostgreSQL and 0.34 ms on SQLite.

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
| Raw writes, 8 writers, PostgreSQL (update / insert tps) | 6,276 / 7,895 | 4,367 / 5,162 (−30% / −35%) | 3,152 / 3,746 (−50% / −53%) | S, plus one row per id value | Best | Grows with every index on the shared table |
| Extra storage (600k items) | – | Small | 652 MB (2.8M rows) | Only the references | – | Per index |
| EF model, both providers, no DDL at runtime | Yes | Yes | Yes | Yes | No | No |
| Limit | – | Slots per type per list | – | Slots per type per list | – | – |

### Measured

| Query | F0 JSON (PostgreSQL / SQLite) | S slots | V pivot | H |
|---|---|---|---|---|
| C1 stage = Negotiation, by close date, first 100 of 500k | 243 ms / 480 ms | 2.9 ms / 1.5 ms | 237 ms (hash joins over 83k and 500k values) | as S |
| C2 amount range and close-date range, count | 114 ms | 4.0 ms | 13 ms | as S |
| C3 board: count per stage | 307 ms / 819 ms | 86 ms / 45 ms | 140 ms | as S |
| C4 deals of one account | 0.17 ms (GIN) / 480 ms | – | 0.21 ms / 0.12 ms | as V |
| T1 my open tasks across 50 lists by due date | ≥ 130 ms / ≥ 152 ms (one list at a time); 9.5 ms as one JSON query on PostgreSQL | – | – | 1.5 ms / 0.53 ms |

### Why H

- On promoted fields the CRM view (C1) ran 84 times (PostgreSQL) and 320
  times (SQLite) faster than on JSON, the range count (C2) 28 times, and the
  board counts (C3) 4 and 18 times.
- V fails exactly where CRM views live: a filter on one field sorted by
  another. The planner hash-joins the value sets instead of walking one index,
  and V costs the most on writes. H uses rows only for id values, where a
  lookup by value is the whole query.
- Promoted fields are not free. With four slots indexed and filled on every
  row, raw updates ran −30% and inserts −35%
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
`json_each` for SQLite, and it is cached per user. Ownership rules add
`OwnerId` to the search document.

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
  2.06 s, blocking every writer in the database. About 250,000 rows
  would pass the 5 s `busy_timeout`, and other writes would start failing.
  10,000 rows took 109 ms (SQLite) and 871 ms (PostgreSQL).
- **Delta:** log `ScopeChanged(scopeId)` instead of a list-wide reset. The
  next delta call returns that scope's items as changed or removed, depending
  on the caller's access, with 410 only above a size limit ([0012](../issues/0012-permission-change-forces-full-delta-resync.md)).
- **Live events:** deliver `item.changed` only to connections whose principal
  set matches the scope's entries ([0011](../issues/0011-item-live-events-reach-every-tenant-user.md)).

## Concurrency notes

PostgreSQL:

- With A, the access check is an index-only read, so reads scale with cores:
  5,289 requests/s with 4 clients on 4 cores, compared with 13
  today.
- Item writes touch only their own rows: no counters on folders or lists
  (checked in `ItemWriter`). Keep it that way; count once instead of storing
  counts ([0001](../issues/0001-list-pages-slow-as-a-folder-grows.md)).
  Optimistic concurrency with `Version` holds no locks between requests.
- Keep write transactions short: fan-out in chunks; search and embeddings
  stay behind the outbox.
- Every index on `items` is paid on every update that changes an indexed
  column. The GIN index on `Fields` makes every field change such an update.
  Keep the set small: primary key, browse, `(ScopeId, Id)`,
  `(ListId, OwnerId, Id)`, the partial slot indexes, and GIN on `Fields`. Once
  hot fields are promoted, check whether GIN still earns its write cost.
- Send the tenant setting once per request or connection, not before each
  query. Under a transaction-mode pooler it must be in the same transaction as
  the query ([0007](../issues/0007-postgresql-rls-session-overhead-under-pooling.md)).
- Later, for very large installs: partition `items` by hash of tenant or list.
  That is DDL in the PostgreSQL migrations only; EF does not see it.

SQLite:

- There is one writer. Long write transactions are the main risk, hence the
  chunked fan-out (10,000 rows held the lock for 109 ms).
- No JSON parsing per row in filters: promote the fields
  ([0010](../issues/0010-sqlite-field-filters-parse-json-per-row.md)).
- Access filter as a subquery (see the per-provider note in decision 1).

## Target model (sketch)

```
lists.items
  Id, TenantId, ListId, ContentTypeId, ParentId, IsFolder, Title
  Fields                 JSON, the source of truth
  ScopeId    not null    list id, or the nearest folder/item with unique permissions
  OwnerId    null        user or group; defaults to the creator
  Text1..4, Number1..2, Date1..4        promoted single-valued fields
  CreatedAt/By, UpdatedAt/By, DeletedAt/By, Version
lists.item_refs     (ItemId, FieldKey, Value) + ListId, TenantId          ids in person, lookup and term fields
lists.acl_entries   (ScopeId, PrincipalKind, PrincipalId) + Level, ListId, WorkspaceId, TenantId
search.documents    + ScopeId, OwnerId;  document_principals removed
```

There are no users yet, so the API can change where that helps:

- permission responses show role principals ("members of the workspace")
  instead of expanded members;
- items get `ownerId`, field definitions `indexed`, lists `itemAccess`;
- break, reset and large moves may answer 202 with an operation;
- migrations are generated again for both providers (no data to migrate).

## Order of work

1. A: principal-set cache, non-null scope, `acl_entries`, `IItemAccess`,
   provider-specific filter shape, tenant-isolation and permission tests, and
   a permission-heavy scenario in `PaperDotNet.Performance` (0002, 0003, 0006).
2. Fan-out: chunked background moves, search by scope, delta scope markers,
   live-event audience (0004, 0008, 0011, 0012).
3. Ownership rules for CRM-style lists.
4. H: slots, `item_refs`, translator mapping, promotion backfill; built-in
   content types promoted (0001, 0005, 0010).
5. Queries across lists as one query: My tasks, calendar, smart folders (0009).

## Open questions

1. **Slot budget:** how many promoted fields per type and list (proposal:
   4 text, 2 number, 4 date)?
2. **Large moves:** accept a few seconds of old access for moved items, or
   block the move until the reassignment finishes?
3. **Groups:** will groups nest, or managers see their reports' records? Only
   then add a group-expansion table (the useful part of C).
4. **Sharing (IAM-08):** break and copy as SharePoint does (supported by A as
   is), or additive shares as Google Drive does, which need a second filter
   term (`OR Id IN @sharedWithMe`)?
5. **GIN on PostgreSQL:** keep it for ad-hoc equality on fields that are not
   promoted, or drop it for write throughput?
