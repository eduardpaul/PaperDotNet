# ADR-0040: Taxonomy-backed graph relationships

Status: Accepted (implemented)

Date: 2026-10-02

ADR-0039 supplies immutable item identity and symmetric links across lists and workspaces. Knowledge graphs also
need predicates and direction: a receipt contains a line, while that line belongs to its receipt. Visibility from
both endpoints does not imply symmetric meaning.

Extend the existing edge with a taxonomy type and optional direction. Untyped symmetric links retain their existing
API and behavior. Symmetric endpoints remain canonically ordered; directed endpoints keep source and target order.
Each edge has its own id. Uniqueness includes endpoints, predicate and direction, allowing different predicates
between the same items. Removing an edge by id leaves other edges intact. Aggregate related-item reads still return
each readable peer once. Directed edges appear on both endpoints; incoming/outgoing filters include only directed
edges. Queries page over edge identity after permission trimming, so hidden endpoints cannot consume a visible page.

Use an open managed term set, Relationships/Types, for predicates. Reuse ITermStore for term identity, synonyms,
labels, translation and curation rather than store arbitrary label strings on edges. Users can create vocabulary
while linking. Store behavior beside the term in Lists: default direction, an optional inverse label, and optional
maximum incoming/outgoing edges per item (directed types only). Behavior is immutable; create a different predicate
for different semantics. An omitted type retains Related. Explicit direction must match a selected predicate.

Maximum endpoint counts include recycled items because recycling preserves relationships. Both endpoints are
version-checked during writes; concurrent creations cannot exceed a predicate's maximum. These limits constrain
edges, not item existence: a newly created line may temporarily have no parent. A minimum or required-parent rule
would need an application-level creation workflow or a transaction spanning creation and linking.

Term renames retain identities. Compatible, unconstrained term merges rewrite predicates and deduplicate identical
edges through the existing TermMerged subscriber. Taxonomy offers an ITermMergeValidator contract so Lists can
reject direction/limit conflicts and prevent merging predicates with endpoint limits while they have edges.
The target's inverse label wins. Deprecation stops new assignments but existing edges retain their labels.

Graph reads/writes retain ADR-0039's tenant isolation and Contribute on both endpoints. Relationships never grant
access. Moves, recycle/restore and purge preserve the same identity and lifecycle behavior. Graph APIs, SDKs, MCP,
templates and Related UI expose types, direction and individual edge identity. Templates reference terms by portable
paths and remap endpoints and types on import; type definitions are a tenant-level module section.

Workflow scripts expose paged items.related, planned items.relate/items.unrelate, and planned items.deleteById.
They run in both the Jint sandbox and TypeScript SDK. The server saves the graph writes in its resumable plan, and
repeating a create/link does not duplicate items or edges. Reads do not see planned writes, matching existing script
semantics. Global deletion resolves an item's current location at apply time.

The receipts package is the first consumer: contains receipt line is directed, has the inverse belongs to receipt,
and permits at most one incoming edge per line. Quantity, unit price and amount stay on each Receipt line. The
reading workflow removes its earlier generated edges/lines and creates new line items and relationships, without a
list-constrained receipt lookup. Reapplying a package remains additive; existing lookup columns are retained for
compatibility; upgrading the earlier sample requires making its lookup optional and backfilling existing edges, as documented in its guide. New readings populate relationships. Product catalogs remain separate from purchase occurrences.

This provides a graph stored through provider-neutral EF on SQLite and PostgreSQL. Graph visualization, arbitrary
multi-hop query languages and required relationships are separate future capabilities. Relationship property bags and workspace queries are implemented by ADR-0041.
