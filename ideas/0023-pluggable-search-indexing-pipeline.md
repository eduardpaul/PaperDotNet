# 0023: Separate indexing/embedding/query, event-driven, pluggable backend

- **Status:** mapped
- **Area:** Search / Platform
- **Date:** 2026-09-27
- **Mapped to:** [features.md](../docs/features.md): SRC-11 (also touches SRC-01, SRC-07, SRC-10)

## The idea

Split the Search module into three architecturally separate concerns, each
behind its own abstraction, instead of one implementation that owns writing,
chunking, embedding and querying together:

1. **Indexing** — turn a changed item into searchable text/passages
   (`ISearchIndex`/`ISearchSource` today).
2. **Embedding** — chunk text and generate vectors for it.
3. **Query** — full-text, semantic and hybrid search over what the first two
   produced.

Today, adding a document indexes it synchronously via `ISearchIndex`, but
passages only get embedded on the next tick of the `search.embeddings` cron
job (up to a minute later, ADR-0027). Instead, chunking and embedding a new
or changed item should be **triggered directly by the item's own change
event**, processed asynchronously (through the existing outbox/event
subscriber pipeline, not a new engine) so it normally completes in seconds,
with the recurring job kept only as a catch-up sweep for failures/missed
messages.

Once the three concerns are separate interfaces, the current PostgreSQL
`tsvector`/SQLite FTS5 + in-process `VectorIndex` implementation becomes one
pluggable backend. A tenant (or the whole install) could later switch to a
dedicated search engine (Meilisearch, Qdrant, etc.) by implementing the same
interfaces, without changing `/v1.0/search`, the SDK, or callers like MCP
(idea 0004) or automation (idea 0009).

## Why / problem it solves

- Clear seams make each piece independently testable, replaceable and
  scalable (e.g. embeddings are the expensive part; indexing is cheap).
- Removes the up-to-a-minute delay between adding a document and it being
  semantically searchable.
- Keeps the "simple by default" install (SQLite/PostgreSQL, no extra
  service, ADR-0009/ADR-0027) while leaving a real path to a dedicated search
  service for tenants/installs that outgrow in-process vector search, without
  an API or SDK break.

## Examples / references

- Current state: [search.md](../docs/search.md),
  [ADR-0012](../docs/adr/0012-full-text-search.md) (full-text),
  [ADR-0027](../docs/adr/0027-semantic-and-hybrid-search.md) (semantic/hybrid;
  its "Consequences" section already flags the external-engine escape hatch
  behind `VectorIndex`).
- Existing pieces to reshape: `ISearchIndex`/`ISearchSource`
  (Search.Contracts), `IFullTextSearch` (per-provider, Persistence),
  `VectorIndex` + `search.embeddings` recurring job (`PaperDotNet.Search`).
- Prior art: OpenSearch/Elasticsearch ingest pipelines vs. query, Meilisearch
  as a swappable engine, Qdrant/pgvector as swappable vector stores.

## Notes

<!-- Open questions to settle during review:
     - "Async" here means the existing outbox + `IEventSubscriber<T>`
       pattern (ADR conventions in CLAUDE.md), not a new workflow engine —
       confirm this is just a new subscriber reacting to the item's change
       event, calling the indexing then embedding abstractions in sequence.
     - New contracts: does `PaperDotNet.Search.Contracts` need
       `IChunker`/`IEmbeddingIndex` alongside `ISearchIndex`, so a backend
       implements indexing and embedding independently (a keyword-only
       backend may not implement the embedding one)?
     - Backend selection: one active backend per install (a setting), or per
       tenant? Migrating a tenant from the built-in backend to an external
       one needs a reindex path (reuses SRC-10).
     - Failure handling: chunk/embed-on-event should be idempotent and retried
       through the outbox; the recurring job remains a periodic reconciliation
       pass (catches anything the event path missed), not the primary path.
     - Does the external-engine option change the "optional, off by default"
       principle (CLAUDE.md) — must remain fully optional, default install
       unaffected. -->
