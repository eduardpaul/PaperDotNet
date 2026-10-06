# Search

`GET /v1.0/search` searches everything the caller may read (documents with
their text, list items, tasks, events, comments): SRC-01…09. Design:
[ADR-0012](adr/0012-full-text-search.md) (full text) and
[ADR-0027](adr/0027-semantic-and-hybrid-search.md) (meaning, hybrid, pages).

Indexing executes through the existing workflow engine. **Index for search**
(`search.index`) is enabled per list/library and runs after saved item changes
and completed file-text extraction. Its steps stage configurable chunks, publish
the current revision, and embed missing vectors. Failures and retries are visible
in workflow history. [ADR-0043](adr/0043-search-indexing-as-workflows-over-a-search-store.md)
describes the source, workflow and store boundaries.

## Indexing controls and status

- Open an item's **Workflows** tab to see current indexing status, text and
  embedding readiness, chunk truncation, and all processing runs. **Index now**
  launches indexing even when automatic indexing is disabled.
- List/library workflow settings control automatic `search.index` execution.
- List settings → **Search** → **Include this list in search** controls complete
  inclusion. Exclusion immediately hides titles, fields, comments and file text
  across every query mode and facets; re-enabling requests item indexing workflows.
- `GET .../lists/{listId}/searchSettings` returns inclusion and an ETag.
  `PUT` changes inclusion with `If-Match`; requires `search.write` and workspace
  Manage permission.
- `GET .../lists/{listId}/items/{itemId}/searchIndex` returns `notIndexed`,
  `running`, `waiting`, `indexed`, `stale`, `failed` or `excluded`, current/published
  revisions, publication time, chunk count, truncation, and latest indexing run.
  `embeddingState` and `contentState` distinguish usable metadata from pending text
  or embeddings. Access follows the current item's permissions.
- Manual launch uses the existing
  `POST .../lists/{listId}/workflows/builtIns/search.index/runs` endpoint.
  A failed embedding step can be retried through workflow retry without publishing again.
- **Rebuild search** (`search.rebuild`) requests bounded pages of child indexing
  runs and waits durably for them. API/CLI reindex progress follows this workflow.
  Its default 15-minute schedule repairs stale/missing publications where
  automation is enabled. Excluded lists are skipped by every rebuild.

Chunk parameters are `chunker` (`window`, `pages`, `whole`), `maxChars` and
`overlap`. Text-only hashes preserve vectors through renames and rebuilds. Keyword
results remain available after an embedding provider failure. Stores do no implicit
chunking, and there is no separate embedding executor.

## Queries

| Parameter | Meaning |
|---|---|
| `q` | Words (all must match), `"exact phrase"`, `a OR b`, `-exclude` / `NOT word`, `prefix*`. Optional when a filter is given |
| `mode` | `keyword`, `semantic` or `hybrid` (below) |
| `workspaceId`, `containerId` (list), `contentTypeId`, `termId` (with child terms), `createdBy`, `updatedFrom`, `updatedTo` | Filters |
| `$filter` | Conditions on the items' fields (below) |
| `$top` (max 100), `$skip` | Paging (`@odata.nextLink`) |

Each hit has:
- `snippet`: text around the match, from the passage that matched best;
- `page`: the page of the document that passage is on, or null for matches
  outside a page, such as the title or fields (SRC-09);
- `matchedBy`: `keyword`, `semantic` or both;
- `rank`: higher is better.

The response also returns `facets` (workspace, list, content type, term),
`@odata.count` and `mode`.

## Filtering by fields

`$filter` filters on the fields of items: their metadata is part of the search
index ([ADR-0043](adr/0043-search-indexing-as-workflows-over-a-search-store.md)).

```
GET /v1.0/search?q=invoice&$filter=fields/amount gt 100 and fields/status in ('open', 'late')
GET /v1.0/search?$filter=fields/due lt 2026-04-01 and fields/paid eq false
```

- **One search field per name.** A field name is one search field across all
  lists, like a managed property: `amount` in two invoice lists is filtered
  as one. If a name is used with different kinds (a number in one list, text
  in another), each kind is addressed as `fields/{name}_{kind}`, e.g.
  `fields/code_number`.
- **Kinds and operators:**

  | Field types | Kind | Operators |
  |---|---|---|
  | text, email, url, choice | keyword | `eq`, `ne`, `in` (exact value) |
  | number, currency | number | `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `in` |
  | date, dateTime | date, dateTime | same as number |
  | boolean | boolean | `eq`, `ne` |
  | person, lookup | reference | `eq`, `ne`, `in` (ids) |
  | managed metadata, keywords | terms | `eq`, `ne`, `in` (term ids) |

  Combine conditions with `and`, `or`, `not` and parentheses. Notes (long
  text) are searched with `q` but not filtered.
- **Multi-value fields** match when any value matches. `ne` matches when none
  does. `eq null` finds items without a value.
- **Unknown fields fail.** A field name is known once an item that has it is
  indexed. Until then, filtering on it returns `400`.
- **After upgrading,** run a reindex (`POST /v1.0/search/reindex` or
  `paperdotnet reindex`) so that existing items get their fields in the index.

## Modes

- **keyword**: full-text search with stemming (SRC-05). The count and facets
  cover every match.
- **semantic**: finds documents by meaning, even when the wording differs or
  the OCR is poor, e.g. "car" finds "automobile repair" (SRC-07).
  - Operators are removed from the query, but excluded words still exclude.
- **hybrid**: keyword and semantic results in one list, fused with reciprocal
  rank fusion (SRC-08). Documents found both ways rank first.
- **Default:** `hybrid` when semantic search is configured, else `keyword`
  (`Search:DefaultMode`).
- **Candidates:** in `semantic` and `hybrid` mode, each side contributes its
  best `Search:CandidateLimit` (200) documents. The count, paging and facets
  cover those candidates only.

## Search stores

Where documents, passages and vectors live (`Search:Store`,
[ADR-0043](adr/0043-search-indexing-as-workflows-over-a-search-store.md)):

| Store | What it is | When to use it |
|---|---|---|
| `database` (default) | Tables in the app's database: SQLite FTS5 or PostgreSQL `tsvector`; vectors searched in memory | Every install, including several servers |
| `zvec` (preview) | An in-process [zvec](https://github.com/alibaba/zvec) collection per tenant under `{Storage:DataPath}/search/zvec`: full text with BM25, HNSW vectors, filters on metadata and fields | One server, large semantic indexes; needs the zvec native library ([ADR-0044](adr/0044-zvec-search-store.md)) |

**zvec settings** (`Search:Zvec` section):

| Setting | Default | Meaning |
|---|---|---|
| `LibraryPath` | none | `libzvec_c_api` file or folder, when it is not next to the app |
| `Languages` | `english` | Languages with stemming. Each costs about 40 MB per open collection, and is fixed when a tenant's collection is created |
| `PrefixSearch` | `true` | `word*` terms, through a trigram column (also about 40 MB). It matches inside words too |
| `MaxOpenCollections` | 4 | Tenants kept open; the least recently used one is closed |
| `MemoryLimitMb` | 1024 | zvec's memory limit |

**Limits of `zvec`:**
- **One server only:** zvec locks the collection's folder.
- **Counts and facets** cover the first 10,000 matches
  (`Search:Zvec:KeywordCandidates`).
- **Changing the embedding model** starts a new collection. Run a reindex
  afterwards.

## Setting up semantic search

Semantic search is off until an embedding model is configured. Any
OpenAI-compatible API works: OpenAI, or a local server such as Ollama,
LM Studio, vLLM or llama.cpp.

```bash
# Ollama in the same compose project: ollama pull nomic-embed-text
PAPERDOTNET__AI__Embeddings__Provider=openai
PAPERDOTNET__AI__Embeddings__Endpoint=http://ollama:11434/v1
PAPERDOTNET__AI__Embeddings__Model=nomic-embed-text

# OpenAI
PAPERDOTNET__AI__Embeddings__Provider=openai
PAPERDOTNET__AI__Embeddings__Model=text-embedding-3-small
PAPERDOTNET__AI__Embeddings__ApiKey=sk-…
```

**How content is embedded:**
- Documents are split into passages of about 1,200 characters, with the page
  each is on. The item workflow activity (`search.embed`) embeds new
  and changed passages in the background.
- Unchanged text is never embedded again.
- Changing the model embeds everything again with the new model. Until that is
  done, semantic results come only from the passages it has already embedded.
- After upgrading, run a reindex (`POST /v1.0/search/reindex` or
  `paperdotnet reindex`) so that existing content gets passages.

**Options** (`Search` section):

| Setting | Default | Meaning |
|---|---|---|
| `Store` | `database` | The search store ([ADR-0043](adr/0043-search-indexing-as-workflows-over-a-search-store.md)): where documents, passages and vectors live and how they are searched. `database` is the only one installed today |
| `MinSimilarity` | 0.3 | Cosine similarity below which a passage is no match; tune it for your model |
| `CandidateLimit` | 200 | Documents per side before fusion |
| `EmbeddingBatchSize` | 64 | Passages per call to the model |

**Limits:**
- Vectors are searched in memory on each server, on SQLite and PostgreSQL
  alike (ADR-0027). A tenant uses about passages × dimensions × 4 bytes, for
  example 50,000 passages × 768 dimensions ≈ 150 MB.
- A tenant's vectors are dropped from memory after 30 minutes without
  searches.
- The query text is sent to the configured model. Choose a local model when
  content must not leave your network.
