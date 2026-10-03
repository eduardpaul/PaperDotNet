# ADR-0043: Search indexing as workflows over a pluggable search store

- **Status:** Proposed (refines [ADR-0012](0012-full-text-search.md) and [ADR-0027](0027-semantic-and-hybrid-search.md); implements SRC-11, [idea 0023](../../ideas/0023-pluggable-search-indexing-pipeline.md))
- **Date:** 2026-10-02

## Context

Different documents need different indexing. A receipt is one chunk with a
summary. A contract should be split by its clauses, with a sentence of context
on each. A scanned letter may need a model to clean its text first. The search
store should be replaceable too, and the main database should not hold the
heaviest derived data.

### How indexing works today

1. **Lists builds the item's search document in code** (`ListItemSearchDocuments`):
   - field text becomes the body, and fields marked high go into keywords;
   - it adds the term ids and the item's scope;
   - Documents adds the file's page texts through `IItemSearchContributor`.
2. **Search chunks it with fixed constants** (`SearchIndex.UpsertAsync`, `Passages`):
   - windows of 1,200 characters, overlapping by 150, at most 400 per document;
   - it writes `search.documents`, `document_tags` and `search.passages` to the main database.
3. **The cron job `search.embeddings` embeds passages every minute.**
   - One model serves the whole server.
   - Vectors are stored as blobs on the passages and loaded into memory on each server.
4. **`SearchService` queries these tables directly** through EF Core and
   `IFullTextSearch`: full-text joins, scope trimming, facets (`GROUP BY`) and
   snippets from passages.

### Problems

1. **The process is fixed code.**
   - The only seam is `IItemSearchContributor`, which can only add text.
   - Chunking, what is embedded and any enrichment cannot vary by library or content type.
   - ADR-0038 moved text, OCR and images into workflows, but indexing stayed hidden:
     `document.readText` calls `IListItemStore.ReindexAsync` as a side effect.
2. **The store cannot be replaced.**
   - `ISearchIndex` covers writes only.
   - Queries (`SearchService`) are EF Core against `SearchDbContext`, so a new
     backend means rewriting search.
3. **The main database holds the heaviest derived data.**
   - A file's text is stored twice (`documents.body` and `passages.text`), each with its own full-text index.
   - The vectors take passages × dimensions × 4 bytes.
   - On SQLite all of this shares the one writer with application writes, and every backup carries it.
4. **Metadata and content are indexed as one unit.**
   - Every item change (a field edit, a comment, an upload) reads all of the
     file's page texts again, splits up to 400 passages and rewrites the document.
   - Several callers do this inside the HTTP request: the upload in
     `DocumentEndpoints` and comments in `CommentEndpoints`. ADR-0012 said
     indexing would never be part of the write.
5. **Content is embedded again more often than needed.**
   - The embedding input is the title plus the passage. Renaming a document
     changes every passage hash, so the whole file is embedded again.
   - `SearchReindexer` deletes each source (`DeleteSourceAsync`), and
     `ListIndexInvalidated` deletes the list's documents, before writing them
     again. That drops the passages with their embeddings, so a reindex embeds
     the whole tenant again. During the refill the source is missing from search.
6. **Long files are cut without a warning.**
   - The body stops at 200,000 characters, and passages stop at 400 (about 480,000 characters).
   - Beyond that, the end of a file has no keyword or page hits.
7. **Each semantic query reads every passage.** `VectorIndex` checks for changes
   with a count and max filtered on `Embedding != null`. No index covers that
   filter, so every query reads all passage rows of the tenant.
8. **The search contract doubles as the item-text API.** AI activities read
   "the text of an item" through `IItemSearchContributor`.

### What stays

- **Scope-id trimming (ADR-0035).** The scope ids the caller can read are a
  query parameter, and search tables are never joined with another module's
  tables. That is what lets the store leave the main database. Every search
  engine can filter on a keyword attribute.
- **`SetScopesAsync`.** A permission change does not touch text or vectors.
- **Embeddings reused by content hash.**
- **`FullTextQuery` and reciprocal rank fusion.** The query is parsed once and
  fused outside the engine.

## Decision

### 1. Three parts, each with its own contract

| Part | Owns | Contract | Default |
|---|---|---|---|
| **Producers** | What an item is: metadata | `ISearchIndex`, `ISearchSource` (Search.Contracts) | Lists, Collaboration (as today) |
| **Index pipeline** | How content becomes chunks, enrichment and vectors | `search.*` workflow activities, `ISearchChunker` | Built-in workflow `search.content`, per library |
| **Search store** | Where chunks and vectors live, and how they are queried | `ISearchStore` (Search.Contracts) | `database` store, in its own database |

`SearchService` serves the API, the MCP tool, facets, fusion and snippets. It
talks only to `ISearchStore`.

### 2. Metadata and content are indexed separately

- **Metadata** holds the id, source type, workspace, list, content type,
  title, keywords, language, scope, terms, author and dates, **and the item's
  fields as typed search fields**.
  - Producers build it in code on item events, as now.
  - It is cheap and always on: an item is searchable within seconds without
    any workflow.
  - Metadata is part of the search store, not looked up elsewhere.
    `SearchDocumentData.Fields` carries each field with a kind (`text`,
    `keyword`, `number`, `date`, `boolean`, `reference`, `terms`). A field can be:
    - **searchable:** its text is in full-text search with its `search` weight;
    - **filterable:** a typed value the store filters on;
    - **queryable:** `/v1.0/search` takes `$filter` on it (`fields/total gt 100`).
  - A field name with one kind is one search field across all lists, like a
    managed property. The same `total` number in two invoice lists is filtered
    as one. `indexed: true` (ADR-0035) asks the store to index the field for
    fast filters.
- **Content** is the chunks of the item's file (later also other long text),
  with a `ContentVersion` (the file version, or a hash of the text).
  - Only the index pipeline writes it.
  - A field edit, a rename or a comment never touches it.
  - The store ignores content older than the version it has, so a slow run
    cannot overwrite a newer file.
- `SearchDocumentData.Pages` and Documents' `IItemSearchContributor` go away.
  Comments stay a contributor (short text, part of the metadata).
- Requests stop indexing directly. `IListItemStore.ReindexAsync` publishes an
  event through the outbox, and the subscriber does the work.
- Reading an item's text for AI moves to its own contract, `IItemText`
  (Lists.Contracts). Documents implements it from the page texts.

### 3. Content indexing is workflow activities

The Search module registers these activities with `AddWorkflowActivity<T>()`.
Steps pass only the document id and a generation id, never the chunks:
workflow runs live in the main database.

| Activity | What it does |
|---|---|
| `search.chunk` | Splits the item's text with a chunker and stages the chunks in the store under the run's generation. Inputs: `chunker`, `maxChars`, `overlap`, `source` (default `pages`), or `chunks` (given, e.g. by a `script` node; size-limited). |
| `search.enrich` | Asks the chat model for text to add to each staged chunk: a sentence of context, a summary or likely questions. Inputs: `instructions`, `scope` (`chunk` or `document`). With `execution: batch` it waits on `ai.batch`. Results are cached by chunk hash + instructions hash. |
| `search.publish` | Makes the staged generation the document's content, so its keyword hits and pages are live, and removes the previous generation. |
| `search.embed` | Embeds the document's chunks that have no vector of the store's model. `immediate` embeds now; `later` leaves them to the sweep. |

**Chunkers** implement `ISearchChunker` (Search.Contracts). The Search module
registers them with `services.AddSearchChunker<T>()`, and extensions use the
same method on `IExtensionBuilder`, gated per tenant.

| Chunker | Splits by |
|---|---|
| `window` (default) | Today's windows: 1,200 characters, 150 overlap, by page |
| `pages` | One chunk per page |
| `paragraphs` | Paragraphs, merged up to the size |
| `headings` | Sections under headings, for structured text and Markdown |
| `whole` | One chunk for the whole document, for receipts and short files |

[`Microsoft.Extensions.DataIngestion`](../dependency-licenses.md) chunkers can
back implementations once that package is stable.

**Item-level AI steps do not change.** `ai.classify`, `ai.extract` and
`ai.summarize` set fields and terms, and the producers index those as
metadata. A flow can branch on what they found and pick a different chunk node
for each kind of document.

**The built-in workflow "Index the content"** (`search.content`):
- Its scope is the library, and it is on by default.
- It is triggered by `wf.documents.text.hasText`, and by hand.
- Its default flow, `chunk → publish → embed`, reproduces today's behavior.
- Its parameters are `chunker`, `maxChars` and `overlap`.
- `document.readText` stops reindexing. It saves the page texts and raises
  `hasText`.
- When a library turns the workflow off, its files' content is not in search.
  Titles and fields still are, as ADR-0038 has it: only what the workflows made
  is served.

```jsonc
// A contracts library: sections with context, embedded at once.
{ "start": "chunk", "nodes": {
  "chunk":   { "activity": "search.chunk",   "inputs": { "chunker": "headings", "maxChars": 2000 }, "next": "context" },
  "context": { "activity": "search.enrich",  "inputs": { "instructions": "One sentence: which contract and clause this is." }, "next": "publish" },
  "publish": { "activity": "search.publish", "next": "embed" },
  "embed":   { "activity": "search.embed" } } }
```

**Runs per file, not per edit.** Workflows run per file version, not per item
edit, so the runs in the main database grow with uploads. Lists without files
(tasks, notes, events) have no content workflow: their text is metadata.

**Embedding follows the pipeline.** `search.embed` embeds as soon as the
content is published. `search.embeddings` remains as a sweep for failures and
model changes, on a slower default schedule (every 15 minutes).

### 4. `ISearchStore` is the backend contract

A sketch (Search.Contracts, so packages and extensions can implement it):

```csharp
public interface ISearchStore
{
    string Name { get; }
    SearchStoreCapabilities Capabilities { get; }   // Keyword, Vector, Facets, Languages, NativeHybrid

    // Writes: metadata from producers, content and vectors from the pipeline.
    Task UpsertDocumentsAsync(IReadOnlyCollection<SearchDocumentData> documents, CancellationToken ct);
    Task StageContentAsync(Guid documentId, Guid generation, IReadOnlyList<SearchChunk> chunks, CancellationToken ct);
    Task PublishContentAsync(Guid documentId, Guid generation, string contentVersion, CancellationToken ct);
    Task SetVectorsAsync(string model, IReadOnlyCollection<ChunkVector> vectors, CancellationToken ct);
    Task SetScopesAsync(IReadOnlyDictionary<Guid, Guid> scopes, CancellationToken ct);
    Task DeleteAsync(SearchSelection selection, CancellationToken ct);  // ids, container, source, or older than a reindex stamp

    // Reusing what was expensive to compute, and copying to another store.
    Task<IReadOnlyDictionary<string, ReadOnlyMemory<float>>> FindVectorsAsync(string model, IReadOnlyCollection<string> inputHashes, CancellationToken ct);
    IAsyncEnumerable<SearchRecord> ExportAsync(CancellationToken ct);

    // Queries: the parsed query, the query vector and the filters, never raw text.
    // The store runs keyword, semantic or hybrid search itself. Capabilities say
    // what SearchService must still do over the candidates (facets, sorting).
    Task<StoreSearchResult> SearchAsync(StoreSearchQuery query, CancellationToken ct);
    Task<IReadOnlyList<SearchFieldInfo>> GetFieldsAsync(CancellationToken ct);  // for $filter
}
```

- **`StoreFilter`** carries the readable scope ids, the workspace, list,
  content type, term ids (`SearchService` resolves the term subtree), author,
  dates and excluded documents. It also carries a **`SearchFilter` tree** for
  field conditions: the store-neutral form of `$filter`, parsed with
  Microsoft.OData against the store's field list. Each store renders it in its
  own language: LINQ for `database`, a filter expression for zvec.
- **Every record and every query carries the tenant.** A shared conformance
  test suite (tenant isolation, trimming, facets, paging, page hits) runs
  against each store.
- **One store per install:** `Search:Store`, `database` by default. A store per
  tenant can come later.
- **Changing the store** runs the operation `POST /v1.0/search/migrate`. It
  copies with `ExportAsync` and computes nothing again. When the old store
  cannot be read, a reindex rebuilds the new one.
- **Other stores are optional packages,** off by default. Each new dependency
  is checked against the license policy.
  - **In process:** zvec ([ADR-0044](0044-zvec-search-store.md)).
  - **External engines:** Meilisearch, OpenSearch, Qdrant with full text, or
    pgvector.

### 5. The default store leaves the main database

- **The `database` store keeps today's tables behind `ISearchStore`:**
  documents, tags, chunks with their full-text index, and vectors. It still
  uses EF Core on both providers (ADR-0009), with migrations in the provider
  projects.
- **It has its own connection string, `ConnectionStrings:Search`:**
  - **SQLite:** a separate `search.db` next to the main database, by default.
    It has its own writer and its own WAL, so application writes never wait
    on indexing.
  - **PostgreSQL:** the `search` schema on the same server, as today. A
    connection string moves it to a database of its own, with RLS policies
    generated as for every module.
- **A file's text is stored once.** It is kept as chunks, with one full-text
  index. A document's keyword rank combines its metadata match and its best
  chunk match.
- **Backups include `search.db` by default,** because enrichment and
  embeddings cost money to compute again. `paperdotnet backup
  --without-search` leaves it out, and a reindex rebuilds it.

### 6. Embeddings are computed once

- **The embedding input is the chunk text plus its enrichment.** The title is
  metadata, so renaming a document embeds nothing again. A chunker or
  enrichment can still put the title into a chunk on purpose.
- **Vectors are keyed by input hash and model.** They are reused across
  reindexes, store migrations and duplicate files.
- **A reindex no longer deletes first.** It writes everything again with a
  reindex stamp, then deletes what it did not see. Search stays complete during
  a reindex, and vectors and enrichment are found again by hash.
- **One embedding model per store**, because vectors of different models
  cannot be compared. A model per tenant (AI-01) comes with a store per tenant,
  not a model per library.
- **The `VectorIndex` change check uses the indexed `EmbeddingModel` and
  `VectorStamp` only,** or a per-tenant stamp row, so it no longer reads every
  passage.

### Phases

1. **The store moves out.** Add `ISearchStore` and the `database` store, move
   `SearchService` onto it, and give it its own connection (`search.db`).
   Behavior does not change. This step alone takes search out of the main
   database.
2. **Metadata and content split.** Requests stop indexing synchronously.
   Reindexing stops deleting first. Embedding inputs drop the title. Long files
   report their truncation instead of cutting silently.
3. **Content indexing becomes workflows.** Add `search.chunk`,
   `search.publish` and `search.embed`, the chunker registry and the built-in
   `search.content`. Remove the reindex from `document.readText`. Add `IItemText`.
4. **Enrichment.** Add `search.enrich` with batch execution, and chunks given
   by scripts.
5. **The first external store,** as an optional package, with
   `/search/migrate`.

## Rejected alternatives

- **One `search.index` activity configured with a whole pipeline.** It hides
  the steps and leaves no place for an AI step or a branch in between.
- **A content workflow on every item change.** Field edits are frequent, and
  each run is stored in the main database. Metadata stays code; only file
  content goes through workflows.
- **`Microsoft.Extensions.VectorData` as the store contract.** It covers
  vectors only, not full text, facets or scope trimming. It can still serve
  inside a store (pgvector, Qdrant).
- **An external ingestion service (Kernel Memory or similar) as the pipeline.**
  It needs a second container in the minimal install, and it would duplicate
  the workflow engine.
- **Different embedding models per library.** Their vectors cannot be ranked
  together, so a query would have to fan out and fuse.

## Consequences

- Libraries choose chunking and AI enrichment as workflows: visible, run
  again by hand, and exported with templates. Extensions add chunkers.
- Moving the store out takes search tables, full-text indexes and vectors out
  of the main database. On SQLite, indexing no longer competes with
  application writes.
- Renames, field edits, comments and reindexes no longer embed again. AI
  enrichment is paid for once per distinct chunk.
- **Ranking changes:** content matches are ranked by chunk, not by one large
  body. This needs a small relevance test set before phase 2 ships.
- **Scope filters on external engines:** a caller can read thousands of scopes
  (unique permissions, ADR-0035), and engines limit filter sizes. A store must
  split the filter or over-fetch and trim. The conformance suite covers it.
- **Turning off "Index the content" removes a library's file text from search.**
  The library settings already list its document workflows; this one joins them.
- **The search store is derived data.** Deleting a tenant must clear it in
  every store.
- **Open questions:**
  - A store per tenant, or only per install?
  - Should `search.enrich` text be shown to users as a hit snippet, or only
    used for ranking?
  - Should the size limits of "Index the content" (today 400 passages) become
    a library setting?
