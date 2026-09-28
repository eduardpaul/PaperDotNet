# ADR-0035: Item storage and permissions at scale

- **Status:** Accepted, not implemented yet
- **Date:** 2026-09-28
- **Replaces:** how ADR-0011 evaluates permissions (its scope model stays) and
  how ADR-0012 trims search.

## Context

Lists must hold a document library of a million items and CRM-style lists of
hundreds of thousands, on SQLite and PostgreSQL, with many concurrent users.
The options and every measurement are in
[item-and-permission-storage.md](../item-and-permission-storage.md). The
benchmark ([`tests/benchmarks/item-storage`](../../tests/benchmarks/item-storage/README.md))
used 1.6M items and was checked through the real `ListsDbContext` with EF Core
10. It found:

- **Every request loads every unique scope of the list.** On a library laid
  out like a Papermerge import (7,500 unique scopes), that is a full scan of
  `items`: 81 ms on PostgreSQL and 386 ms on SQLite through EF, before the page
  query. List pages top out at about 12–18 requests/s on 4 cores
  (issues 0002, 0003).
- **Fields in JSON cannot be indexed** for sorting, ranges or grouping. A
  filtered, sorted CRM view of 500k rows takes 160 ms (PostgreSQL) and 475 ms
  (SQLite), where typed columns take 2 ms. On SQLite every equality filter
  parses the JSON of every row (issues 0005, 0010).
- **A permission change rebuilds whole lists:** the scope rewrite runs in the
  request (100k rows hold SQLite's write lock for 1.5 s), the list's search
  index is rebuilt and embedded again, and delta clients resync
  (issues 0004, 0008, 0012).
- **Queries across lists repeat all of this per list** (issue 0009).
- **EF Core 10 sends `list.Contains(x)` as one parameter per value on SQLite**,
  and SQLite then picks a bad plan (639 ms instead of 6 ms).
- **The RLS tenant setting costs one round trip per query:** EF opens a
  connection per query outside a transaction (issue 0007).

## Decision

### 1. Permissions: scope ACL, looked up by principal

- **Scopes stay.** Every item has a `ScopeId`: the nearest folder or item with
  unique permissions, or the list id when it inherits from the list. It is
  never null, and the property is a `Guid`, not `Guid?` (with a nullable
  property EF adds an `array_position(@allowed, NULL)` term on PostgreSQL). An
  item has unique permissions when `ScopeId == Id`.
- **`lists.acl_entries` replaces `permission_grants`:** `ScopeId`,
  `PrincipalKind`, `PrincipalId`, `Level` (a number, so `>=` works in SQL),
  `ListId`, `WorkspaceId`, `TenantId`. The key is
  `(ScopeId, PrincipalKind, PrincipalId)`, with an index on
  `(PrincipalId, ListId, ScopeId, Level)`.
- **Principal kinds:** user, group, and the workspace roles as
  pseudo-principals (visitors, members, owners of workspace W). Guests,
  sharing links and "everyone" can be added later as more kinds.
- **Inheriting lists** have three entries: visitors → Read, members →
  Contribute, owners → Manage. Breaking inheritance copies these role entries,
  not the members one by one, so people who join the workspace later still
  get access. Workspace membership changes write nothing.
- **Full control:** every scope of a workspace has a fixed owners → Manage
  entry that cannot be edited. Holders of `workspace.manage` skip the filter.
- **Principal set:** the caller's user id, all their groups (including groups
  that contain their groups, section 2) and their workspace roles. It is
  cached per tenant and user in `HybridCache` and invalidated by tag when
  users, groups or workspace members change.
- **Allowed scopes:** one index-only query,
  `SELECT ScopeId, MAX(Level) FROM acl_entries WHERE PrincipalId IN (@principals)
  [AND ListId = @list] GROUP BY ScopeId` (1–2 ms through EF with 202 scopes).
  A scope that is not in the set means no access. The list's other scopes are
  never loaded.
- **Filter:** `EF.Parameter(allowed).Contains(i.ScopeId)`. EF sends one array
  on PostgreSQL (`= ANY (@allowed)`) and one JSON parameter on SQLite
  (`json_each`). Plain `allowed.Contains(…)` is not used on this path.
- **Contract:** `IItemAccess` in Lists.Contracts gives other modules the
  principal set, the allowed scopes of a list or of the tenant, and the
  filter. Search, Tasks, Calendar, smart folders and MCP use it, including one
  query across many lists instead of one query per list.
- **Sharing (IAM-08) breaks and copies**, as SharePoint does. Sharing an item
  or folder gives it its own scope: a copy of the inherited entries plus the
  new person or group. Later changes to the parent's permissions do not reach
  it.

### 2. Groups can contain groups

- A group member is a user or a group. Adding a group that would create a
  cycle is rejected. Nesting is limited to 10 levels.
- `identity.group_closure(AncestorId, GroupId)` holds every group and every
  group it is inside of, including itself. Nesting changes rebuild the
  tenant's closure in the same transaction (groups are few).
- Every consumer of membership uses the closure: the principal set (section 1),
  roles assigned to groups (`EffectiveScopeProvider`), `GetGroupIdsAsync`,
  `GetGroupMembersAsync` (group inboxes, notifications, automation), search
  and provisioning templates (nested groups by name).
- ACL entries still name the group that was granted, so nesting changes write
  no ACL rows. They only invalidate the cached principal sets and scopes of
  the tenant.
- Reverse-proxy sign-in keeps managing direct memberships only.

### 3. Fields: JSON as the source, promoted columns and a value table

- `Fields` (JSON) stays the source of truth for validation, versions and the
  API. The GIN index on it stays on PostgreSQL.
- **Promoted single-value fields.** `items` gets 10 text, 10 number and 10
  date columns (`Text1..10`, `Number1..10`, `Date1..10`), each with a partial
  index `(ListId, slot, Id) WHERE slot IS NOT NULL`.
  - A field definition gets `indexed: true`. Text, choice and boolean fields
    use text slots; number and currency use number slots; date and date-time
    use date slots.
  - How many a list may use is configurable per type
    (`Lists:IndexedFields:Text`, `:Number`, `:Date`, default 10, at most the
    columns in the schema). More columns need a migration.
  - Built-in content types have fixed slots (task status, priority and due
    date; event start and end), so queries across lists use the same column.
  - `ItemWriter` fills the slots in the same transaction. Promoting a field on
    an existing list is a background backfill (`IOperations`); until it ends,
    queries on that field use the JSON.
  - Measured: a list pays only for the slots its rows fill. With 30 slots in
    the schema, rows filling 3 cost about 28% of raw write throughput and
    rows filling all 30 cost 54–69% (PostgreSQL, 8 writers). The absolute
    rates (3,000+ updates/s) are far above what the API serves.
- **Multi-value and reference fields** (multi-select choice, person, lookup,
  managed metadata, keywords, single or multiple) go to
  `lists.item_values(ItemId, Field, Value)` with `ListId` and `TenantId`, when
  indexed:
  - `Field` is a small number per content type field. `Value` is a `Guid`:
    the id for people, lookups and terms; for choices, a name-based UUID of
    the field and the choice text, mapped back to the text through the field
    definition. One typed column serves all of them.
  - The key is `(ItemId, Field, Value)`, with an index on
    `(ListId, Field, Value, ItemId)`. `ItemWriter` writes only the values that
    changed.
  - Filters: `any` (one of), `all` (every one of) and `not any` (none of) on
    these fields; filters on a term include its child terms. A selective
    value is filtered with `ItemId IN (subquery)`, a common one with
    `EXISTS`, a negation with `NOT EXISTS`. The translator decides with a
    count on the index capped at 2,000 rows (under 0.2 ms). SQLite needs this
    choice: each form alone took up to 1.4 s on the other kind of value. With
    it, every filter measured (500k items) took under 15 ms on both databases,
    most under 1 ms.
  - New: counts per value (board columns, group-by folders, facets), where an
    item counts in each of its values, and reverse lookups ("deals of this
    account", "tasks assigned to me" across lists). Multi-value fields still
    cannot be sorted.
  - Indexed multi-value fields accept at most 100 values per item.
  - Term merges (`TermMerged`) and deleted lookup targets update these rows
    along with the JSON.
  - Cost: about 240 bytes per value on PostgreSQL (2.7M values ≈ 650 MB, both
    indexes). Fields that are not indexed stay in the JSON only.
- **Paging (issue 0001):** a browse index `(ListId, ParentId, IsFolder, Title,
  Id)`, keyset paging on it, the count on the first page only, and a
  case-insensitive title index for the search box.

### 4. Derived data and fan-out

- **Search** stores `ScopeId` on each document and trims with the caller's
  allowed scopes for the tenant (`IItemAccess`). `document_principals` is
  removed. A grant change writes nothing to the index. A scope rewrite
  updates one column of the affected documents and keeps their passages and
  embeddings.
- **Grant changes** write the scope's entries only, evict caches and publish
  `ScopeAccessChanged(scopeId)`.
- **Break inheritance:** first write the new scope's entries (a copy of the
  parent's), then move the subtree to the new scope. **Reset:** first
  overwrite the scope's entries with the parent's, then move the subtree back,
  then delete the entries. Access is correct at every step.
- **Moves across scopes allow a short window.** Up to 5,000 items are
  reassigned in the request. Larger subtrees are reassigned by an
  `IOperations` job (202) in chunks of about 2,000 rows, one transaction each.
  Until a chunk is done, its items keep their old access, typically for a few
  seconds.
- **Delta** logs `ScopeChanged(scopeId)` instead of a list-wide reset. A delta
  call returns that scope's items as changed or removed for the caller, and
  answers 410 only above a size limit.
- **Live events** go only to connections whose principal set matches the
  scope's entries, not to every user of the tenant (issue 0011).

### 5. Connections

The tenant setting is sent once per request and context, not before every
query: a module context opens its connection on first use and keeps it until
the request ends. Pool pressure (several contexts per request) is measured
before this applies to every module (issue 0007).

## Consequences

- The access check no longer grows with the list: list pages went from about
  12–18 to 5,000+ requests/s in the benchmark. Unique scopes (shared folders,
  Papermerge home folders, IAM-08 sharing) cost only their ACL rows.
- Promoted fields make CRM views, boards, date ranges, reverse lookups and
  "mine" across lists index lookups on both databases. Each promoted field
  costs write throughput and, for value-table fields, about 240 bytes per
  value. Owners choose what to index, within the configured limits.
- Large moves leave moved items with their old access for a few seconds.
- A group-nesting change invalidates the tenant's cached access: acceptable
  because such changes are rare.
- More moving parts in Lists (ACL, slots, value table, background
  reassignment) and in Identity (group closure).
- There are no users yet: migrations are generated again for both providers
  and the API changes where needed. Permission responses show role principals
  instead of expanded members, field definitions get `indexed`, groups accept
  groups as members, and break, reset and large moves may answer 202.
- The CLAUDE.md rules on item access (`schema.Access.Level`,
  `schema.Access.Filter`) change with the implementation.

## Order of work

1. ACL by principal: non-null `ScopeId`, `acl_entries`, principal-set cache,
   `IItemAccess`, the `EF.Parameter` filter, tenant-isolation and permission
   tests, a permission-heavy scenario in `PaperDotNet.Performance`.
2. Nested groups and the group closure.
3. Fan-out: background reassignment, search by scope, delta scope markers,
   live-event audience.
4. Promoted columns and the value table, with the translator and backfill.
5. Queries across lists as one query (My tasks, calendar, smart folders).
6. The per-request tenant setting on PostgreSQL.
