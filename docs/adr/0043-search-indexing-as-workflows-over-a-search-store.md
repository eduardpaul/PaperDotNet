# ADR-0043: Search indexing as workflows over a pluggable search store

- **Status:** Accepted (refines ADR-0012 and ADR-0027; implements SRC-11–14)
- **Date:** 2026-10-02; execution and inclusion decision revised 2026-10-06

## Context

Search queries already use a replaceable store, but indexing previously ran in
item-event subscribers and direct reindex calls. Stores split text implicitly,
and a cron job embedded passages. None of this produced search workflow history.
Renaming an item invalidated its content embeddings, and a source rebuild deleted
existing results before refilling them. The original proposal's always-on
metadata and content-only opt-out did not satisfy complete library exclusion.

## Decision

Sources, execution, and storage have distinct responsibilities:

| Part | Responsibility | Contract |
|---|---|---|
| Lists | Current metadata, typed fields, terms, scope | `ISearchItemSource` |
| Documents | Current version's text and extraction readiness, independent of search inclusion | `IItemTextSource` |
| Collaboration | Comment text | `IItemSearchContributor` |
| Workflows | Trigger delivery, durable runs, retry, replacement, waits and completion | `Workflows.Contracts` |
| Search | Policy, source revisions, chunking, publication, embedding and rebuild activities | `search.*` activities |
| Store | Prepared chunks, stages, vectors, filters, facets and queries | `ISearchStore` |

The built-in **Index for search** (`search.index`) runs per list or library,
automatically after item changes and completed text extraction. Its flow is
`search.chunk` → `search.publish` → `search.embed`. Turning automatic runs off
retains manual launch. A separate inclusion setting excludes the entire list
from keyword, semantic, hybrid and filter-only queries, including counts/facets.
The setting is enforced by queries immediately and rechecked during publication;
removal of stored records runs through `search.containers`. Re-enabling inclusion
requests item workflows even when automatic indexing is off.

Chunking is configurable (`window`, `pages`, `whole`, window size and overlap).
Stores receive prepared chunks; passage hashes cover text independently of title.
Unchanged chunks retain vectors during metadata edits and rebuilds. The 400-chunk
limit produces a visible truncation indicator. Workflow outputs contain references,
counts and hashes rather than full text or vectors.

Publication stages a generation identified by run ID. Before publishing, the
activity checks the current source revision, policy version and generation claim.
Database publication uses a transaction on both supported providers. The zvec
store stages to a sidecar file; its external writes cannot join the relational
transaction, so query-time inclusion and current item eligibility remain mandatory.
A retry after a committed publication can recognize its existing publication;
embedding failures preserve keyword results and retry only the failed step.

**Rebuild search** (`search.rebuild`) is a coordinator on `WorkflowRequests`.
- It requests item indexing in pages and waits for each page through durable
  completion waits. A failed item is counted, not fatal.
- Explicit requests may launch opted-in built-ins while their automation is off;
  disabled user workflows stay disabled.
- The 15-minute reconciliation requests only items whose publication is missing
  or stale, and only where automation is on. It compares cheap source stamps
  (`ISearchItemSource.GetStampsAsync`, `IItemTextSource.GetRevisionsAsync`),
  chunk settings and the embedding model in batches, never reading text, and
  yields after a bounded scan.
- The API/CLI reindex operation follows these visible workflow runs.

Permission changes do not index again. `search.scopes` moves the published
items' scopes, and the revision leaves the scope out. Steps with nothing to do
(the list is excluded, the item is gone or superseded) end at a `skipped` port,
not as failures.

**System workflows.** Running every product reaction to saved items as a workflow
needed engine support so these runs stay cheap and safe:
- **System and required built-ins:** they work past the people-workflow depth
  limit and for folders where asked, run in their own queue, keep successful runs
  for 24 hours, and cannot be deleted. Required ones cannot be turned off and are
  not exported.
- **Item-event context owned by the engine:** activities cannot be handed a
  forged event through raised data.
- **Completion events only when followed:** `wf.*.completed` / `failed` only when
  a workflow listens, `WorkflowRunFinished` only for triggers with a completion kind.
- **Request completion:** once all of a request's runs have ended, or at once
  when none matched.
- **Content-type filters in reaction triggers:** note and task reactions create
  no runs elsewhere.

All product reactions to item add/update/delete/restore are such workflows:
search, note links, item activity, follower alerts, API change notification
queueing, recurring tasks and task-completed announcements. Durable message
transport, delivery jobs, retention and purge cleanup remain infrastructure.

## Consequences

- Users can inspect indexing and all item-processing runs, launch indexing on
  demand, and retry failures through the existing workflow API/UI.
- Index status compares the current revision with the published revision. It
  reports text readiness and embedding readiness separately from keyword availability.
- Source text remains available to AI when a library is excluded from search.
- Item visibility is checked page by page against the items as they are now
  (`IItemAccess.FilterReadableAsync`, bounded by the page), while scopes and
  removal catch up. Counts and facets can include such items for a few seconds.
- Per item change, each matching system workflow costs one run row and one
  message (no completion events nobody follows). Successful system runs are
  deleted after a day.
- Passage hashes no longer include the title: the first rebuild after upgrading
  embeds every passage once more.
- SQLite and PostgreSQL persist policy, publication and staged-generation records;
  both providers have migrations. zvec persists heavy staged payloads outside the DB.
- Existing deployments get enabled defaults on first trigger/settings access;
  rebuilding populates revision status for historical records.
- There is one combined metadata/content generation. Reading current file text on
  metadata edits is still necessary, though unchanged passages avoid re-embedding.
  Separate metadata/content publication and richer custom chunking activities can
  be added through the same contracts without adding another executor.
