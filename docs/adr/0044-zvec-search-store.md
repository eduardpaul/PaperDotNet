# ADR-0044: zvec as an in-process search store

- **Status:** Proposed (builds on [ADR-0043](0043-search-indexing-as-workflows-over-a-search-store.md))
- **Date:** 2026-10-03
- **Plan:** [search-zvec-plan.md](../search-zvec-plan.md)

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
- **Collections with a dynamic schema.** Scalar columns can be added later
  (`STRING`, `BOOL`, `INT32/64`, `UINT32/64`, `FLOAT`, `DOUBLE`, and arrays of
  each), with inverted indexes, including range indexes.
- **Dense and sparse vectors** with HNSW, IVF or flat indexes, and optional mmap.
- **Native full-text search** with BM25:
  - tokenizers `standard`, `ngram`, `jieba` and `whitespace`;
  - filters `lowercase`, `ascii_folding`, and Snowball `stemmer` with a language;
  - queries with phrases, `+`/`-` and `AND/OR/NOT`.
- **Filter expressions** (`==`, `!=`, `<`/`>`, `IN`, `NOT IN`, `BETWEEN`,
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
- **Text columns with full-text indexes:**
  - `title`, `keywords` and `text`, unstemmed;
  - `text_{language}` with a stemmer, added the first time a language appears;
  - `all`, which holds one constant token so a filter-only search can run as
    a query.
- **Field columns `f_{name}_{kind}`** (`f_total_n` DOUBLE, `f_vendor_s` STRING,
  `f_due_d` INT64, `f_owner_r` string array):
  - added with the dynamic schema the first time a field appears;
  - given an inverted index when the field is `indexed: true`;
  - capped per tenant at `Search:Zvec:MaxFieldColumns`. Beyond the cap a field
    stays searchable but is not filterable, and administrators see that.
- **Vectors:** `embedding` (FP32, cosine, HNSW). A model change adds a new
  vector column, fills it, then drops the old one.

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
  escaping builder, never from raw input. Scope trimming is
  `scope_id IN (…)`, and every query adds `published == true`.
- **Modes, each one multi-route query:**

  | Mode | Routes | Fusion |
  |---|---|---|
  | keyword | full text on `title`, `keywords`, `text` and `text_{lang}` | weighted |
  | semantic | vector on `embedding` | none |
  | hybrid | the keyword routes plus the vector route | RRF (k = 60) |
  | filters only | full text on the `all` token | none, then sorted by date |

- **Rows are grouped by document.** A document's score is that of its best
  row, and the best chunk gives the snippet and the page (SRC-09).
- **Counts, facets and paging cover the candidates** (`CandidateLimit`):
  zvec has no aggregation and no offset. The response says when the count is
  an estimate. Exact counts over all items are what list queries are for.

### Interop and packaging

- `[LibraryImport]` declarations for the C API, `SafeHandle`s for native
  objects, and errors from `zvec_get_last_error` as exceptions. No managed
  dependency is added.
- We build `libzvec_c` from a pinned zvec tag in our CI, for linux-x64 and
  linux-arm64 (osx-arm64 and win-x64 for development), and ship it in the
  container image.
  - **The build leaves out libaio (LGPL)** by disabling DiskANN or using the
    `pread` backend.
  - The statically linked libraries are recorded in THIRD-PARTY-NOTICES.

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
  an external engine. At startup the store takes an exclusive lock file on its
  directory. A second server then fails at once instead of damaging the index.
- **Native code runs in the process,** so a crash in zvec stops the server.
  That is the reason it is opt-in first, with a conformance suite, fuzzed
  filter and query builders, and a pinned version. zvec is pre-1.0; the
  wrapper keeps its API changes inside one project.
- **Counts and facets are estimates** beyond `CandidateLimit` in every mode,
  not only in hybrid mode as today.
- **Questions for the spike:** these decide the layout before any production
  code is written. See the plan.
  - Can a vector column be empty (null) until the row is embedded?
  - Does `update` change only the columns it is given?
  - Does full-text search support prefix queries?
  - How fast is `IN` with thousands of scope ids?
  - Does a query see writes made since the collection was opened, in the same
    process?
