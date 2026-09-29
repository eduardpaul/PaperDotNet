# ADR-0035: Item storage and permissions at scale

- **Status:** Accepted; steps 1–4 implemented (ACL by principal, nested groups, fan-out, indexed fields)
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

## Implementation notes

### Step 1: ACL by principal (2026-09-29)

Done as decided, with these details:

- **Role principal ids** are derived from the workspace id and the role
  (`WorkspaceRolePrincipals.Id`: SHA-256, RFC 9562 version 8), so one
  `PrincipalId IN (@principals)` lookup covers users, groups and roles. The key
  is `(ScopeId, PrincipalId)`: principal ids never collide, so the kind is not
  part of it. The lookup index is `(PrincipalId, ListId, ScopeId, Level,
  TenantId)`; `TenantId` is there so the tenant filter does not leave the
  index.
- **New lists get the three role entries** in `ListsDbContext`, whichever path
  creates them (endpoints, templates, home libraries). A permission change
  updates the scope's entries in place (`Acl.Replace`), in one save.
- **Workspace owners and administrators** still skip the filter in a list
  (their workspace level is Manage); the fixed owners entry makes the same
  true for `IItemAccess.GetScopesAsync` across lists.
- **API:** permission entries show roles as `workspaceVisitors`,
  `workspaceMembers` and `workspaceOwners` with the workspace id. The enum is
  `AclPrincipalType`, because Identity's `PrincipalType` (user, group) keeps
  its name. Owners cannot be removed or lowered. Templates name roles
  (`<Grant Role="Members" …/>`, `{"role": "members"}`).
- **Principal cache:** `ItemAccess` caches the set per tenant and user for a
  minute under `AccessCacheTags.Principals(tenant)`. Identity evicts it with its
  scope cache (they share the tag), and `WorkspacesDbContext` evicts it when
  memberships change or a workspace is added. `IWorkspaceAccess` gained
  `GetMembershipsAsync(userId)`, so the set does not depend on the current user.
- **HybridCache runs a factory on the thread pool when the token can be
  cancelled**, without the request's tenant and user. The principal cache,
  and Identity's scope cache (where this was a latent bug: after an eviction
  it could cache "no scopes" for a minute), now pass `CancellationToken.None`.
- **Search** keeps principals on documents until step 3: a document lists the
  principals of its scope's entries, roles as `SearchPrincipals.Role`.
- **Existing databases:** the migration sets `ScopeId` to the list id for
  inheriting items and drops `permission_grants`. Lists created before it get
  no role entries (no data to migrate), so only owners and administrators see
  them until their permissions are set again.
- **Measured:** `PaperDotNet.Performance` has a `shared` scenario: a member
  without full control pages a list whose folders have unique permissions.

### Step 2: nested groups (2026-09-29)

- **Data:** `identity.group_nestings(GroupId, MemberGroupId)` holds the groups
  inside a group; `identity.group_closure(GroupId, AncestorId)` holds every group
  with each group it is inside of, itself included. `IdentityDbContext` rebuilds
  the tenant's closure in the same save whenever a group or a nesting is added or
  removed, whichever path made the change. The migration adds the self rows of
  existing groups.
- **Rules** (`GroupGraph`): a group cannot contain itself, a nesting that would
  make a cycle is refused, and a chain of groups is at most 10 long. The API
  answers 409 `invalidNesting`.
- **API:** `GET/POST /groups/{id}/groups` and
  `DELETE /groups/{id}/groups/{memberGroupId}`. `/groups/{id}/members` still
  lists the users added to the group directly. Removing the last path to an
  administrator role is refused like removing a membership.
- **Consumers:** `GetGroupIdsAsync` (item access, search, group inboxes) and
  `GetGroupMembersAsync` (notifications, automation) include nested groups;
  so do roles assigned to groups (`EffectiveScopeProvider`) and the last-
  administrator check. ACL entries keep naming the granted group, so nesting
  changes write no ACL rows; they evict the tenant's cached principals and
  scopes.
- **Templates:** `<Member Group="…"/>` puts a group inside a group; exporting a
  group also exports the groups inside it. The schema now also allows
  `<Grant Role="Visitors|Members|Owners"/>` (missing from step 1).
- **Web:** the group panel in administration lists the groups inside and adds or
  removes them.
- Reverse-proxy sign-in keeps managing direct memberships only.

### Step 3: fan-out (2026-09-29)

- **Search:** documents carry `ScopeId`; search trims with
  `IItemAccess.GetScopesAsync(null)` as one parameter. `document_principals`
  and `SearchPrincipals` are gone. A grant change writes nothing to the index;
  items that move get `ItemScopesChanged` and `ISearchIndex.SetScopesAsync`
  updates the column. Existing indexes need one reindex after the upgrade.
- **Moves** (`ScopeMover`): level by level, 2,000 items per transaction, each
  with its delta rows and its search event. Items in the recycle bin move but
  are not logged. The walk passes through items that already moved, so it can
  resume. A request moves up to `Lists:ScopeMoveInlineLimit` (5,000);
  `CompleteFolderScopeChange`, saved atomically with the folder change, moves
  the rest. Different from the decision: no `IOperations` job and no 202. The
  message is already written in the same transaction as the change (an
  operation would be a separate write), and the endpoints keep their responses.
  A chunk that moves nothing (the request and the message ran at once) logs
  nothing.
- **Reset** copies the parent's entries onto the scope, moves the items back
  and deletes the scope's entries once no item uses it. **Break** writes the
  new scope's entries with the item, then moves.
- **Grant changes** write entries only. There is no `ScopeAccessChanged`
  integration event: nothing subscribes to it yet (search needs nothing, delta
  reads the change log, principals are not affected).
- **Delta:** `ItemChange.FromScopeId` records where a moved item came from; a
  changed access list logs `ScopeChanged` (not for scopes created or removed
  in the same save, whose items are logged as they move). Delta answers with
  the items as changed, or removed with reason `changed` when the caller could
  read them before; a marker over `Lists:DeltaScopeLimit` items (1,000) is
  410. For a marker, delta cannot tell whether the caller had the items and may
  report ids the caller never saw as removed (ids only).
- **Live events:** `LiveEvent.Audience` carries the principals of the item's
  scope; `/me/events` compares it with the user's principals
  (`IPrincipalSet`, reloaded at most once a minute) and the PostgreSQL
  backplane forwards it.

### Step 4: indexed fields (2026-09-29)

- **Storage:** `FieldDefinition.Indexed`; `items` has `Text1..10` (512
  characters), `Number1..10` (`double`) and `Date1..10` (the canonical text of
  dates and date-times, which sorts in order), each with a partial index;
  `lists.item_values(ItemId, Field, Value)`. A list's places are in
  `ListDefinition.IndexedFields` (JSON).
- **Kinds:** text (up to 512 characters), choice and boolean (`"true"`/`"false"`)
  in text columns; number and currency in number columns; date and date-time in
  date columns; people, lookups, terms (single or multiple) and multiple choices
  in the value table. `note` cannot be indexed. Numbers are `double` in the
  column (SQLite cannot order decimals); the JSON keeps the exact value.
- **Planning** (`FieldIndex.Plan`) runs in `ListsDbContext` whenever a list is
  created, its content types change, or a content type it uses changes. Kept
  fields keep their place; the task and event fields take fixed places (status
  `Text1`, priority `Text2`, due date `Date1`, start `Date2`, end `Date3`,
  assignees value field 1, attendees 2) so queries across lists can use one
  column. Fields over a list's limit stay unindexed rather than failing the
  change. Value field numbers are never reused.
- **Writes:** every save fills the columns and value rows of saved items,
  whichever path saved them (term merges included); purged items lose their
  rows. An indexed multi-value field takes at most 100 values.
- **Backfill:** `IndexedFieldBackfillJob` (every 20 seconds per tenant) fills
  new places for existing items with direct updates, so item versions, audit and
  delta stay as they are; until a place is ready, queries read the JSON.
  Different from the decision: a recurring job, not an `IOperations`
  operation, because planning happens inside a save, where no operation can be
  started, and a job cannot be forgotten by a code path.
- **Queries:** ready columns replace the JSON in filters and sorting; value
  fields answer `eq`, `ne`, `in` and `any` (and so "all of" with `and`, "none
  of" with `not`), with `IN` or `EXISTS` chosen by a count capped at 2,000.
  Multi-value fields cannot be sorted.
- **Counts:** `GET …/items/counts?field=…&$filter=…` counts readable items per
  value (an item counts for each of its values; items without one count as
  null). Multi-value fields must be indexed to be counted.
- **Paging (issue 0001):** the count is on the first page only; the browse
  index `(ListId, ParentId, IsFolder, Title, Id)`; orders made of `isFolder`
  and `fields/title` continue after the last row. No case-insensitive title
  index: the search box does `contains`, which no b-tree index serves.
- **Web:** the field editor has an "Indexed" option.
