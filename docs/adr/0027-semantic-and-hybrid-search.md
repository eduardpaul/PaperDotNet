# ADR-0027: Semantic and hybrid search with page-level hits

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Phase 6 starts with SRC-07 (semantic search), SRC-08 (hybrid ranking) and
SRC-09 (page-level hits). The technical approach planned pgvector with
`Microsoft.Extensions.VectorData`. But:
- PostgreSQL is optional and SQLite is the default install. Everything must
  work on both (ADR-0009).
- pgvector is an extra server extension that plain PostgreSQL images lack.
- SQLite vector extensions (sqlite-vec) are native, pre-release binaries.
- AI providers must be optional and off by default.

## Decision

- **Passages.** `search.passages` holds windows of about 1,200 characters,
  overlapping by 150 and ending at word boundaries, each with its page
  number:
  - first the keywords and body (no page), then each page of the file (from
    `ItemSearchContent.Pages`, supplied by Documents);
  - their own full-text index (FTS5 or `tsvector`, with the document's
    language), so keyword hits can point to a page;
  - a SHA-256 of the embedded text (title + passage). An unchanged passage
    keeps its row and embedding when a document is re-indexed.
- **Embeddings.** The building block `PaperDotNet.AI` creates an
  `IEmbeddingGenerator` (`Microsoft.Extensions.AI`, with logging and
  OpenTelemetry) from `AI:Embeddings`:
  - `none` (the default) or `openai`, meaning any OpenAI-compatible endpoint:
    OpenAI, Ollama, LM Studio, vLLM or llama.cpp. One adapter covers local and
    cloud models.
  - The recurring tenant job `search.embeddings` embeds passages whose
    `EmbeddingModel` differs from the configured model. That covers new and
    changed passages, and every passage after a model change.
  - Embeddings are stored normalized, as float32 bytes, next to the passage.
  - Provider failures stop the run; the next run retries.
- **Vector search in process.** `VectorIndex` keeps each tenant's embeddings
  in memory on every server and searches them by brute force
  (`TensorPrimitives.Dot`), the same on SQLite and PostgreSQL.
  - Before each search it compares the count and the newest `VectorStamp` of
    the model's embeddings with its copy, then loads only what changed. A
    30-second window covers clock skew between servers, so no messages are
    needed across servers.
  - Workspace and list filters apply in memory. Security trimming and the
    other filters apply afterwards in SQL, to the candidate documents.
  - Excluded query words (`-draft`) also remove semantic candidates.
- **Modes and fusion.** `mode=keyword|semantic|hybrid`.
  - Hybrid takes each side's best `CandidateLimit` (200) documents and fuses
    them with reciprocal rank fusion (k = 60).
  - The default is hybrid when a model is configured, else keyword.
  - Count and facets of the fused modes cover the candidates.
- **Page hits.** A hit's snippet and `page` come from its best passage: the
  best full-text-ranked passage for keyword matches, else the most similar
  passage. `matchedBy` tells the client how the hit was found.

## Consequences

- One code path on both databases, and no database extension. Semantic search
  is fast enough for self-hosted tenants: tens of thousands of passages are a
  few milliseconds per query. Memory grows with the passages (passages ×
  dimensions × 4 bytes), and each server holds its own copy.
- For much larger tenants, the index can later move to pgvector (HNSW) or an
  external engine behind `VectorIndex`, without changing the API.
- Query text and passages go to the configured model. Local models keep them
  in the network.
- Passages duplicate document text (and its FTS index). Existing content gets
  passages on the next reindex.
- AI-01 (per-tenant providers) can reuse `PaperDotNet.AI`; today the provider is
  configured per server.
