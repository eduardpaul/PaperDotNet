# ADR-0041: Relationship attributes and workspace graph queries

Status: Accepted (implemented)

Date: 2026-10-02

Global graph edges can carry properties of the connection itself: provenance, confidence, role or ordering.
Store an optional `attributes` JSON object on ItemRelation using the existing provider-neutral JSON document
mapping. Existing edges migrate to `{}` and version 1. Attributes do not participate in edge identity or uniqueness.
Repeating POST retains existing attributes. PATCH explicitly merges keys; null removes a key, and requires the edge's
ETag. GET and graph pages expose the edge version. Successful updates touch both endpoints and publish the existing
relatedItems change events, preserving move/delete concurrency checks and workflow notifications.

Keep the first contract flat: strings, finite numbers and booleans, at most 64 keys and 16 KiB of JSON. Keys are ASCII
identifiers, up to 128 characters; extension prefixes use underscores (`receiptReader_confidence`). Dots are excluded
because OData interprets dotted path names as qualified type casts. Nested objects, arrays, dates and independently
permissioned attributes are not supported. Both endpoints share one bag and the existing edge permissions.

GET /v1.0/workspaces/{workspaceId}/relationships returns an edge once when either current endpoint belongs to the
workspace and the caller can read both. Each entry includes both items and their current locations. Moves immediately
change workspace membership; recycled items are excluded until restored. Unknown or unreadable workspaces return 404.
There is no unscoped cross-tenant graph endpoint.

Workspace scope, endpoint access and attribute filters run in the database before cursor paging. Reuse OData parsing
with an open attributes complex type and a dedicated expression translator. Support eq/ne for scalar values and missing
keys, numeric lt/le/gt/ge, and and/or/not, plus id and directed comparisons. A non-null comparison matches only attributes
of the literal's JSON type; missing or differently typed attributes do not match, including ne. `eq null` matches an absent
key. Null is never stored. Attribute-to-attribute comparisons, nested paths, functions and arbitrary multi-hop queries are
outside this contract. Filters have a 4096-character bound and use the parser's complexity limits and a 64-level expression depth bound. Ordering stays edge-id
ascending; next links retain filters and type/direction selections. Type and directed can also be supplied as query options.

Add type-safe scalar JSON extraction to Persistence and both provider translators, returning SQL NULL on a wrong JSON
kind. This prevents PostgreSQL cast failures and SQLite coercion differences for an open vocabulary. Reuse existing item
JSON extraction unchanged. PostgreSQL containment indexing is available; numeric range filters may need dedicated indexes
in a future workload-driven change. No claims of indexed arbitrary scalar ranges are made.

IListItemStore exposes query, read and attribute patch methods for extensions. Workflow scripts in Jint and the TypeScript
SDK expose items.relationships(options), attributes on items.related results, an optional attributes argument to items.relate,
and versioned items.updateRelationship. Writes stay in the resumable plan; an attribute patch replay is satisfied only if the
edge is exactly one version newer and the supplied keys already match. Other stale writes fail. Reads never see planned writes.
MCP offers equivalent query/patch tools. SDKs are regenerated; the UI is unchanged.

Portable packages carry attributes on relatedItems references and preserve them on remapped edges. Taxonomy merges combine
non-conflicting properties of duplicate edges; incompatible values or a combined bag exceeding limits reject the merge.
The asynchronous subscriber rechecks conflicts. A conflicting concurrent edit causes retry rather than silently dropping data;
resolve the attributes before retrying delivery. No attribute version history or independent field schemas are added.
