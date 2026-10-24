# ADR-0047: Functional parity with PaperGo

Status: Accepted (2026-10-23)

## Decision

PaperDotNet and [PaperGo](https://github.com/eduardpaul/PaperGo) target the same
user-visible feature set and similar behavior. PaperGo's current implementation
is the reference for bringing its capabilities into PaperDotNet. The comparison
baseline, outstanding differences and acceptance criteria live in
[the parity register](../papergo-parity.md).

PaperDotNet keeps its .NET modular monolith, EF Core, tenant isolation, SQLite
and PostgreSQL providers, transactional outbox, extension contracts and generated
SDKs. Native REST routes, JSON casing, configuration keys, package
formats and implementation libraries may differ. ADR-0048 adds a separate exact
PaperGo-compatible organization database; its stored identifiers and schemas
follow the reference, while existing module storage retains tenant isolation. Matching a route count or
copying a Go storage design does not establish functional parity.

Existing PaperDotNet capabilities remain supported, including multitenancy,
OAuth/password/passkey authentication, tasks, calendar, comments, notifications
and notes. A feature absent from PaperGo is recorded as a capability to align in
the other direction, rather than removed from PaperDotNet.

## Verification

Each requirement needs observable acceptance scenarios: authorized success,
denied access, tenant isolation, stale ETags, rollback/recovery and discovery in
the UI where relevant. Changes to API contracts update OpenAPI, all generated
clients and their consumers together. Database changes include EF migrations for
both SQLite and PostgreSQL.

The parity register distinguishes implemented changes, source-mapped overlap,
partial implementations and missing capabilities. Source inspection alone is
not a cross-implementation behavioral certification. Unimplemented requirements
must remain visible until their acceptance scenarios pass.

## Initial lifecycle changes

New lists and libraries, including the Documents template and personal Home
libraries, default to metadata version history off. Owners can enable it, and
explicit template/request settings continue to apply. Existing libraries keep
their settings and retained history. File-byte version storage is independent
of this metadata-history switch.

Deleting a populated folder is explicit: ordinary deletion rejects it;
`recursive=true` requires write permission throughout its live subtree. All
deletion hooks are checked before tombstones, audit and outbox events are saved
atomically. Recycle-bin recovery and retained versions remain available.

## Consequences

ADR-0048 supplies a compatible EF persistence engine for publishing, immutable
schema history and archiving. Integrating the existing HTTP/UI consumers and
WebDAV remains substantial product work across querying, search, files,
permissions and packages. They are tracked as separate requirements instead of being represented
by flags that leave existing readers or background jobs inconsistent.
