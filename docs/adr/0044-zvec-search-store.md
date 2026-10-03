# ADR-0044: zvec as an in-process search store

- **Status:** Proposed (builds on [ADR-0043](0043-search-indexing-as-workflows-over-a-search-store.md))
- **Date:** 2026-10-03
- **Plan:** [search-zvec-plan.md](../search-zvec-plan.md)
- **Spike:** [zvec-spike](../performance-artifacts/zvec-spike/README.md) (2026-10-03): go, with the layout
  changes below.

## Context

ADR-0043 puts search behind `ISearchStore`. Indexing writes metadata, chunks
and vectors there, and `SearchService` queries only the store. Item fields are
part of the metadata: searchable, filterable and queryable (`$filter`).

The `database` store keeps all of this in EF Core tables, a separate database,
but still a relational one. Three things there are hand-built:
- semantic search, brute force in memory on every server;
- page hits, through a second full-text index on passages;
- per-field filters, which would need JSON queries or a value table.

[zvec](https://github.com/alibaba/zvec) (Alibaba, **Apache-2.0**, v0.7, 2026)
is an embedded vector database, "the SQLite of vector databases". It runs in
the process like SQLite, needs no server, and provides:
- **Collections with typed columns:** `STRING`, `BOOL`, `INT32/64`,
  `UINT32/64`, `FLOAT`, `DOUBLE`, and arrays of each, with inverted indexes,
  including range indexes. Only numeric columns can be added to an existing
  collection (spike).
- **Dense and sparse vectors** with HNSW, IVF or flat indexes, and optional mmap.
- **Native full-text search** with BM25:
  - tokenizers `standard`, `ngram`, `jieba` and `whitespace`;
  - filters `lowercase`, `ascii_folding`, and Snowball `stemmer` with a language;
  - queries with phrases, `+`/`-` and `AND/OR/NOT`.
- **Filter expressions** (`=`, `!=`, `<`/`>`, `IN`, `NOT IN`, `BETWEEN`,
  `LIKE`, `CONTAIN_ANY`, `CONTAIN_ALL`, `IS NULL`), pushed down to the indexes.
- **Multi-route queries** (several full-text and vector routes) fused by RRF
  or weighted reranking, plus group-by-field vector queries.
- **Writes:** `upsert`, `update`, `delete`, `delete_by_filter`, `fetch`, an
  iterator, `flush` and `optimize`, with a WAL.
- **A C API** (`c_api.h`), which .NET can call without a binding of its own.

## Decision

**Add `zvec` as an optional search store** (`Search:Store=zvec`), in its own
project, `PaperDotNet.Search.Zvec`. It implements the whole `ISearchStore`
contract, so search runs entirely in zvec:
- keyword, semantic and hybrid search;
- metadata filters and `$filter`;
- scope trimming and page hits.

**Nothing outside the project knows about zvec.**
- Producers, the index workflows, `SearchService`, the API, the SDK and MCP
  only see `ISearchStore`.
- The `database` store stays the default until zvec passes the same
  conformance suite and a soak period.

### Layout

- **One zvec collection per tenant**, under
  `{data}/search/zvec/{tenantId}`. Tenants are isolated on disk, and deleting
  a tenant deletes its directory.
- **One row per document head, one row per chunk.**
  - The head row holds the title, keywords and field text.
  - Chunk rows hold content (text, page, embedding).
  - Every row carries the metadata columns, so one filter expression covers
    head and chunk rows.
- **System columns:** `doc_id`, `kind`, `source_type`, `workspace_id`,
  `container_id`, `content_type_id`, `scope_id`, `term_ids` (array),
  `created_by`, `updated_at` (unix ms), `published`, `generation`,
  `content_version`, `page`, `input_hash`, `emb_model`. Each has an inverted
  index where filters use it.
- **Text columns with full-text indexes,** all created with the collection
  (zvec adds only numeric columns later):
  - `title`, `keywords` and `text`, unstemmed;
  - `text_{language}` with a Snowball stemmer, one per supported language;
  - `prefix`, a trigram column (`ngram` 3–3) for `word*` terms, which the
    full-text grammar lacks. It matches substrings, a little more than prefixes;
  - `all`, which holds one constant token so a filter-only search can run as
    a query.
- **Field values, two forms:**
  - **Text-kind and boolean values** (keyword, reference, terms, boolean) are
    tokens `{kind}:{name}:{base64url(value)}` in one string-array column,
    `field_tokens`, with an inverted index.
    - Equality is `CONTAIN_ANY`; "has a value" is a `{kind}:{name}` token.
    - Values never appear raw in filter text: zvec cannot match a literal
      backslash.
  - **Number, date and time values** each get a column, `n_{name}` (DOUBLE)
    or `t_{name}` (INT64: days, or Unix ms), added the first time the field
    appears, with a range index. zvec allows 1,024 scalar columns. Beyond
    `Search:Zvec:MaxNumericFields` a field is no longer filterable by range,
    and administrators see that.
- **Vectors:** `embedding` (FP32, cosine, HNSW). zvec cannot add a vector
  column later, so the collection is created with the configured model's
  dimension. A model change builds a new collection generation (rows copied
  with the iterator, then embedded) and switches to it.

### Writes

- **One writer per collection,** a channel per tenant. zvec allows one writing
  process, and batching is cheaper.
- **Metadata:** an upsert of the head row, and an update of the metadata
  columns on that document's chunk rows. Text and vectors stay untouched.
- **Content** (`search.chunk` / `search.publish`): chunk rows are written with
  `published = false` under a generation. Publishing marks them published and
  removes other generations (`delete_by_filter`).
- **Scopes, deletes, containers, sources:** `update` and `delete_by_filter`.
- **Vectors:** `update` of `embedding`, `emb_model` and `input_hash`. Reuse
  across reindexes is a filter on `input_hash`.

### Queries

- **Filters** are built from `StoreFilter` and the `SearchFilter` tree by one
  builder, never from raw input.
  - Literals are ids (hex), our own column names and encoded field tokens.
  - Scope trimming is `scope_id IN (…)`, and every query adds
    `published = true`.
- **Modes, each one multi-route query:**

  | Mode | Routes | Fusion |
  |---|---|---|
  | keyword | full text on `title`, `keywords`, `text` and `text_{lang}` | weighted |
  | semantic | vector on `embedding` | none |
  | hybrid | the keyword routes plus the vector route | RRF (k = 60) |
  | filters only | full text on the `all` token | none, then sorted by date |

- **Rows are grouped by document.** A document's score is that of its best
  row, and the best chunk gives the snippet and the page (SRC-09).
- **`matchedBy`:** a fused result has one score per row. It comes from one
  keyword route and one vector route limited to the page's documents
  (`doc_id IN (…)`).
- **Counts, facets and paging cover the candidates** (`CandidateLimit`):
  zvec has no aggregation and no offset. The response says when the count is
  an estimate. Exact counts over all items are what list queries are for.

### Interop and packaging

- `[LibraryImport]` declarations for the C API, `SafeHandle`s for native
  objects, and errors from `zvec_get_last_error` as exceptions. No managed
  dependency is added.
- We build `libzvec_c_api` from a pinned zvec commit in our CI, for linux-x64
  and linux-arm64 (osx-arm64 and win-x64 for development), and ship it in the
  container image.
  - **Pinned, hashed archives:** Arrow's bundled dependencies are fetched
    from our own mirror and checked against their hashes.
  - **libaio (LGPL)** is only loaded with `dlopen` when present. It is not
    linked, and we do not ship it.
  - **Notices:** the statically linked libraries are recorded in
    THIRD-PARTY-NOTICES. Two need a policy decision: zlib (zlib License) and
    Boost (BSL-1.0), both from Arrow (see the spike).
- **String arrays are always passed as `zvec_string_t*` pointers.** zvec
  guesses the packed form from the byte size.
- **The binding never calls `zvec_collection_destroy`,** which deletes the
  collection, except to drop a tenant's index; `zvec_collection_close`
  releases a handle.

## Consequences

- **Search leaves the relational database completely.**
  - Text, metadata and vectors live in zvec files, and backups copy the
    directory after a flush.
  - The in-memory `VectorIndex` is no longer needed with zvec: the HNSW index
    is mmap-able and bounded by `zvec_config_data_set_memory_limit`.
- **Item fields can be filtered in search.** One index serves text, meaning
  and metadata.
- **zvec is for one server.** zvec writes from one process only, so
  installations with several servers (ADR-0025) keep the `database` store or
  an external engine. zvec locks a collection's directory (`LOCK`), so a
  second server fails to open it instead of damaging the index.
- **Native code runs in the process,** so a crash in zvec stops the server.
  That is the reason it is opt-in first, with a conformance suite, fuzzed
  filter and query builders, and a pinned version. zvec is pre-1.0; the
  wrapper keeps its API changes inside one project.
- **Counts and facets are estimates** beyond `CandidateLimit` in every mode,
  not only in hybrid mode as today. `topk` is at most 100,000.
- **Write batches are not atomic** (the spike's crash test). Every write is an
  idempotent upsert or update, retried by the outbox.
- **Spike measurements:** over 200k rows on 4 cores, top 200,
  - 4–18 ms without scope filters;
  - 16–26 ms with 1k–5k readable scopes;
  - about 60–75 ms with 20k readable scopes.
