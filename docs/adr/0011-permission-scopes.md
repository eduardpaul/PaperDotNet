# ADR-0011: Permission inheritance with security scopes

**Status:** Accepted (2026-09-24)

## Context
IAM-07 asks for permissions that flow workspace → list → folder → item, with
the option to break inheritance. Queries over list items must trim what a
user may not see (and later full-text search must too, SRC-04), on SQLite and
PostgreSQL, without recursive SQL.

## Decision
- **Levels:** Read, Contribute, Manage (the existing `WorkspaceAccessLevel`).
  Workspace roles map to them: visitor → Read, member → Contribute,
  owner → Manage. Workspace owners and administrators (`workspace.manage`)
  always have **full control**, including content with unique permissions,
  so nobody can lock them out.
- **Grants** (`lists.permission_grants`): principal (user or group) + level on
  a list or item that has unique permissions. Breaking inheritance copies the
  inherited grants by default (SharePoint behaviour); resetting deletes them.
- **Security scope** (SharePoint `ScopeId`): every item stores the id of the
  nearest item (itself or a folder above it) with unique permissions, or null
  when permissions come from the list. Breaking/resetting inheritance or
  moving a folder rewrites the scope of the inheriting subtree.
- **Evaluation:** per request, `ListSchemaLoader` computes the user's level
  for the list and for each unique scope of the list (few rows). Queries add
  `ScopeId == null || allowedScopes.Contains(ScopeId)`, a plain indexed
  filter on both providers. Search will reuse the same scope ids.
- A list with unique permissions is invisible (404) without a grant. Grants
  to users who are not workspace members have no effect until sharing
  (IAM-08) adds limited access.

## Consequences
- Trimming is one `IN` filter, no recursive queries.
- Changing permissions on a large folder updates all inheriting descendants
  (batched `ExecuteUpdate` per level, in one transaction).
- Every item endpoint checks `schema.Access.Level(item.ScopeId)`; new
  endpoints must do the same (listed in CLAUDE.md).
