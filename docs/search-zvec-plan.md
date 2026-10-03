# Plan: search on zvec, with metadata in the index

Status: **proposed**. Decisions: [ADR-0043](adr/0043-search-indexing-as-workflows-over-a-search-store.md)
(decoupled indexing, `ISearchStore`) and [ADR-0044](adr/0044-zvec-search-store.md) (zvec store).

## Goal

- **Search runs by calling zvec.** Keyword (BM25), semantic (HNSW), hybrid
  (RRF), filters, scope trimming and page hits all run in zvec. With
  `Search:Store=zvec`, no search data stays in an EF Core database.
- **Metadata is part of search.** Indexing moves every item field into zvec:
  - **searchable:** in full-text search, with its weight;
  - **filterable:** a typed column;
  - **queryable:** `$filter` on `/v1.0/search`.
- **It follows the decoupled design.** Producers and index workflows write
  through `ISearchIndex`, and `SearchService` reads through `ISearchStore`.
  zvec stays inside `PaperDotNet.Search.Zvec`, and the `database` store remains
  the default and the fallback.

**Not in scope:** making zvec the default, which is decided after a soak
period; external engines; installations with several servers on zvec.

## Target shape

```
Producers (Lists, Collaboration)            Index workflows (search.chunk / enrich / publish / embed)
        │ SearchDocumentData + Fields                │ chunks, vectors
        ▼                                            ▼
             ISearchIndex  (Search module: validation, field catalog, hashing, vector reuse)
                                   │
                                   ▼
             ISearchStore  (Search.Contracts)  ◄──── SearchService (API, MCP, $filter, facets over candidates)
              │                         │
   DatabaseSearchStore (EF, default)   ZvecSearchStore (PaperDotNet.Search.Zvec)
                                        │ LibraryImport
                                        ▼
                                   libzvec_c (native, pinned, built in our CI)
```

**Module rules (enforced by architecture tests):**
- Only `PaperDotNet.Search.Zvec` loads the zvec native library.
- That project references only `PaperDotNet.Search.Contracts` and
  `PaperDotNet.Abstractions`.
- The host registers it when `Search:Store=zvec`.

## Contracts (Search.Contracts)

```csharp
// Metadata fields on the document a producer pushes.
public sealed record SearchDocumentData(...)
{
    public IReadOnlyList<SearchField> Fields { get; init; } = [];
}

public enum SearchFieldKind { Text, Keyword, Number, Date, DateTime, Boolean, Reference, Terms }

/// <param name="Weight">Full-text weight (none, normal, high); text of the field goes to keywords or body accordingly.</param>
/// <param name="Indexed">Ask the store for a fast filter index (ADR-0035 <c>indexed: true</c>).</param>
public sealed record SearchField(string Name, SearchFieldKind Kind, JsonNode? Value, SearchWeight Weight, bool Indexed);

// Store-neutral filter tree: what $filter parses into, and what each store renders.
public abstract record SearchFilter;
public sealed record FieldCompare(string Field, SearchFieldKind Kind, CompareOp Op, JsonNode Value) : SearchFilter;  // eq ne gt ge lt le
public sealed record FieldIn(string Field, SearchFieldKind Kind, IReadOnlyList<JsonNode> Values) : SearchFilter;     // in (...), any(...)
public sealed record FieldIsNull(string Field, SearchFieldKind Kind) : SearchFilter;
public sealed record And(IReadOnlyList<SearchFilter> Items) : SearchFilter;
public sealed record Or(IReadOnlyList<SearchFilter> Items) : SearchFilter;
public sealed record Not(SearchFilter Item) : SearchFilter;

public sealed record StoreSearchQuery(
    SearchMode Mode, FullTextQuery? Text, ReadOnlyMemory<float>? Vector, string? VectorModel,
    StoreFilter Filter, int CandidateLimit, IReadOnlyList<string>? Languages);

public sealed record StoreSearchResult(IReadOnlyList<StoreHit> Hits, bool Truncated, SearchFacets? Facets);

public sealed record StoreHit(Guid DocumentId, double Score, IReadOnlyList<string> MatchedBy, int? Page, string? PassageText,
    StoreDocumentSummary Document);   // workspace, list, content type, terms, title, author, updated: hits and facets without a second read
```

**Where the shared types go:**
- `FullTextQuery` moves from Persistence to `PaperDotNet.Abstractions`, which
  Persistence and Search.Contracts both reference already. A store package
  then needs no Persistence reference.
- Search.Contracts gets its own `SearchWeight` (none, normal, high). Lists
  maps `FieldSearchWeight` to it, so Search.Contracts does not depend on Lists.

### Mapping list fields to search fields (Lists, `ListItemSearchDocuments`)

| List field type | Search kind | zvec column | Filter operators |
|---|---|---|---|
| `text`, `email`, `url` | `Keyword` (+ text by weight) | `f_{name}_s` STRING | `eq ne in`, `startswith` → `LIKE 'x%'` |
| `choice` (single / multiple) | `Keyword` | `f_{name}_s` / `f_{name}_ss` STRING array | `eq in` / `any` |
| `note` | `Text` only | none (text goes to `text`) | none; searchable only |
| `number`, `currency` | `Number` | `f_{name}_n` DOUBLE | `eq ne gt ge lt le` |
| `date` | `Date` | `f_{name}_d` INT64 (days since 1970) | same |
| `dateTime` | `DateTime` | `f_{name}_t` INT64 (unix ms) | same |
| `boolean` | `Boolean` | `f_{name}_b` BOOL | `eq` |
| `person`, `lookup` | `Reference` | `f_{name}_r` STRING array (GUID `N`) | `eq` / `any` |
| `managedMetadata`, `keywords` | `Terms` (+ labels to keywords) | `f_{name}_m` STRING array; also `term_ids` | `eq` / `any`, with child terms (expanded by `SearchService`) |

- **One column per name and kind.** A field whose kind differs from another
  field of the same name gets its own column; the suffix keeps them apart.
- **`$filter` addresses `fields/{name}`.** When the name exists with several
  kinds, the literal's type picks the column.

## zvec layout

The layout is in ADR-0044. In short:
- **one collection per tenant;**
- **one head row per document and one row per chunk,** with the metadata on
  every row;
- **system columns, text columns and `f_*` field columns** added with the
  dynamic schema;
- **one `embedding` vector column.**

## Work packages

### WP0: spike (go / no-go), about 3 days

Build `libzvec_c` from the pinned tag **without libaio** (DiskANN off or the
`pread` backend) for linux-x64. Then answer each question below with a
throwaway console app that calls the C API through `LibraryImport`.

| # | Question | If the answer is no |
|---|---|---|
| S1 | Can a row exist with no vector, so chunks are searchable before they are embedded? | Two collections: `rows` (text and metadata) and `vectors` (chunk id, embedding, metadata copy) |
| S2 | Does `update` change only the given columns? | Metadata edits write the whole chunk rows again, read with `fetch` |
| S3 | Does full-text search support prefix queries (`inv*`)? | Use an `ngram` column for prefix search, or reject `*` in zvec mode |
| S4 | How fast is `scope_id IN (…)` with 1k, 5k and 20k ids over 1M rows? | Pre-filter by workspace, fetch more candidates, and trim in .NET |
| S5 | Does a query in the same process see writes made since open, without reopening? | Flush after each write batch and reopen readers on a schedule |
| S6 | Can a multi-route query tell which route matched, for `matchedBy`? | Run each route alone for the final page only |
| S7 | Is a full-text query on a constant token, plus a filter, fast enough for filter-only search? | Filter-only search goes to list queries when one list is selected, and has no global browse |
| S8 | What happens after `kill -9` during writes: WAL recovery and an intact collection? | No-go until fixed upstream |
| S9 | Which libraries are statically linked into `libzvec_c`, and what are their licenses? | Leave the library out of the build, or no-go |

**Exit:** a short report in `docs/performance-artifacts/`, ADR-0044 updated
with the answers, and the layout fixed.

### WP1: contracts and the `database` store

- `ISearchStore` (ADR-0043 §4) with `SearchAsync`, the types above, and
  `SearchFieldInfo`.
- `DatabaseSearchStore` implements it from the current `SearchIndex` and
  `SearchService` code:
  - field values in a `search.document_fields` value table (doc, field,
    kind, string, number and long columns, indexed by field and value), the
    same idea as ADR-0035;
  - `SearchFilter` rendered as LINQ.
- `SearchService` keeps parsing, mode choice, term subtrees, the query
  embedding, facets when the store does not compute them, and paging. It
  calls only `ISearchStore`.
- **Store conformance suite** (`tests/PaperDotNet.IntegrationTests/Search/StoreConformance*.cs`):
  - an abstract test class, run per store;
  - covers tenant isolation, scope trimming, every field kind and operator,
    child terms, keyword, semantic and hybrid search, page hits, paging,
    deletes by id, container and source, scope changes, generations,
    reindex stamps, and export and import.

**Done when** the current search tests and the conformance suite pass on
SQLite and PostgreSQL. API behavior does not change.

### WP2: metadata from producers and `$filter`

- `ListItemSearchDocuments` emits `Fields` using the mapping table. Field
  text still goes to keywords or body by weight.
- Moving metadata into an existing index needs a reindex: run it once on
  upgrade and say so in the release notes.
- Add `$filter` to `GET /v1.0/search`, parsed with Microsoft.OData (already
  used by Lists).
  - The EDM is built from `ISearchStore.GetFieldsAsync`: a `fields` complex
    property with one property per name and kind.
  - The tree is translated to `SearchFilter`.
  - Unknown fields or operators return `400` through `ApiErrors`.
- MCP `search` tool: a `filter` argument.
- SDK: regenerate. Web: a field filter row on the search page (8d), through
  the SDK only.
- Tests:
  - `$filter` on every kind;
  - tenant isolation for the new parameter (CLAUDE.md: every new endpoint
    parameter gets one);
  - trimming still applies when `$filter` matches documents in unreadable
    scopes.

### WP3: zvec binding (`PaperDotNet.Search.Zvec/Native`)

- `ZvecNative.cs`: `[LibraryImport("zvec_c")]` for the functions we use.
  - collection: create, open, close, flush, optimize, add_column, create_index;
  - documents: create, add field, set pk;
  - writes: upsert, update, delete, delete_by_filter, fetch;
  - queries: vector, FTS and multi-query creation, RRF and weighted
    reranking, filter, topk, output fields;
  - the iterator, `get_last_error`, and the free functions.
- `SafeHandle`s for the collection, document, query and result lists.
  Strings are UTF-8, and every error code becomes a `ZvecException` with the
  last error.
- `ZvecLibrary.Initialize`: called once, with the memory limit, query threads
  and log path from `Search:Zvec`.
- `ZvecCollection` (managed): the operations above, with blocking calls made
  in `Task.Run` and a bounded number of parallel queries.
- **`ZvecFilterBuilder`:** the only place filter text is made.
  - Strings are quoted and escaped, GUIDs use the `N` format, and numbers use
    the invariant culture.
  - Fuzz and injection tests feed it every character class.
- **`ZvecFtsQueryRenderer`:** turns `FullTextQuery` into zvec query strings
  (`+word`, `"phrase"`, `(a OR b)`, `-word` only alongside a positive term).
- **Native build:** `.github/workflows/zvec-native.yml`.
  - Builds the pinned tag for linux-x64 and linux-arm64 (osx-arm64 and
    win-x64 on demand) with libaio off.
  - Publishes `runtimes/{rid}/native/libzvec_c.*` as a build artifact that
    the host project and the Docker image take in.
  - Development machines without the library use the `database` store.
    Tests that need zvec skip with a clear reason unless
    `PAPERDOTNET_TEST_SEARCH_STORE=zvec`.

### WP4: zvec store, writes

- `ZvecCollections`: an open collection per tenant directory, closed when
  idle (`Search:Zvec:IdleMinutes`), plus the exclusive directory lock (one
  server).
- `ZvecSchema`:
  - creates the system and text columns;
  - adds `f_*` columns and `text_{lang}` columns on first use, with an
    inverted index when `Indexed`;
  - enforces `MaxFieldColumns`;
  - caches the schema per tenant.
- `ZvecWriter`: one channel per tenant. It batches `UpsertDocuments`,
  `StageContent`, `PublishContent`, `SetVectors`, `SetScopes` and `Delete`,
  then flushes.
  - Head pk `{docId:N}`.
  - Chunk pk `{docId:N}.{generation:N}.{ordinal}`.
  - The head row records the published generation and its chunk count, so a
    metadata update can address the chunk rows.
- `FindVectorsAsync`: a filter on `input_hash IN (…) AND emb_model == m`,
  with vectors returned.
- `ExportAsync`: the iterator with vectors.
- Recurring job `search.zvec.optimize` (nightly, per tenant): compacts deleted rows.

### WP5: zvec store, queries

- `ZvecSearchStore.SearchAsync`:
  1. Build the filter: `published == true AND scope_id IN (…) AND <StoreFilter> AND <SearchFilter>`.
  2. Build the routes for the mode (ADR-0044 table), with
     `topk = CandidateLimit` and the output fields needed for hits and facets.
  3. Run one multi-query.
  4. Group the rows by `doc_id`. Take the best row's score, and the best
     chunk's page and text.
  5. Fetch head rows for documents found only through chunks, for titles
     and facet fields.
  6. Return `Truncated` when the routes hit `topk`.
- `Capabilities`: keyword, vector, hybrid native, facets false (computed by
  `SearchService` over the candidates), languages from the Snowball list.
- Filter-only search: the `all` route, sorted by `updated_at` in .NET.
- Run the conformance suite on zvec, and the relevance set from ADR-0043 on
  both stores; compare the results and record them.

### WP6: index pipeline on zvec

This needs ADR-0043 phases 2–3, which are store-neutral.
- `search.chunk`, `search.publish` and `search.embed` call `ISearchIndex`,
  so they work on zvec unchanged.
- The embedding sweep finds chunk rows whose `emb_model` differs from the
  current model, through the `all` route.
- A model change adds an `embedding_{hash}` vector column, fills it, swaps
  it with the old one, then drops the old one (dynamic schema).

### WP7: operations and documentation

- **Backup:** `paperdotnet backup` pauses the tenant's writers, flushes, and
  copies `search/zvec`. Restore puts the directory back.
  `--without-search` skips it.
- **Store migration:** the `POST /v1.0/search/migrate` operation
  (`database` → `zvec`, or back) runs `ExportAsync`, then writes to the new
  store, then switches; the server restarts on the new store.
- **Observability:** an OpenTelemetry activity per store call, and metrics
  for query latency, candidates, rows written, collection size and open
  collections.
- **Memory:** add zvec to the memory footprint review (mmap on, memory limit
  set).
- **Documentation:** `search.md` (choosing a store, zvec limits),
  `dependency-licenses.md` (zvec and its static libraries),
  THIRD-PARTY-NOTICES, `technical-approach.md` §9, and `features.md` SRC-11
  status.

### WP8: default or not (decision)

Make `zvec` the default for new single-server installations only if:
- the conformance suite is green on every release for 4 weeks;
- there are no native crashes in the soak;
- relevance is at least as good as the `database` store;
- memory and latency are within the performance baseline.

## Order and size

| WP | Depends on | Size |
|---|---|---|
| 0 Spike | none | S |
| 1 Contracts, database store, conformance | none (in parallel with 0) | L |
| 2 Metadata and `$filter` | 1 | M |
| 3 Binding and native build | 0 | M |
| 4 zvec writes | 1, 3 | M |
| 5 zvec queries | 2, 4 | M |
| 6 Pipeline on zvec | 5 and ADR-0043 phases 2–3 | S |
| 7 Operations and documentation | 5 | M |
| 8 Default decision | 7 and the soak | none |

WP1 and WP2 pay off without zvec: metadata becomes filterable in search on
the `database` store too.

## Risks

| Risk | Mitigation |
|---|---|
| zvec is pre-1.0; its API and file format may change | Pin the tag; only `Native/` sees the C API; on an upgrade, export and import or reindex |
| A native crash stops the server | Opt-in; fuzzed builders; conformance suite; `database` store as fallback; out of process later if needed |
| One writing process | Single-server only; directory lock; several servers keep `database` or an external engine |
| No aggregation or offset: counts, facets and deep paging cover the candidates only | `Truncated` flag in the response; exact listing stays with list queries |
| Large scope-id `IN` lists | Spike S4; workspace pre-filter and over-fetch |
| A stemmed column per language widens the schema | Columns only for languages in use; unstemmed `text` always present |
| LGPL libaio in default Linux builds | Our own build without it (S9); license check in CI |
| Field column count | `MaxFieldColumns`; filterable status shown to administrators |
