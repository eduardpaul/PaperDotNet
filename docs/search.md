# Search

`GET /v1.0/search` searches everything the caller may read (documents with
their text, list items, tasks, events, comments): SRC-01…09. Design:
[ADR-0012](adr/0012-full-text-search.md) (full text) and
[ADR-0027](adr/0027-semantic-and-hybrid-search.md) (meaning, hybrid, pages).

## Queries

| Parameter | Meaning |
|---|---|
| `q` | Words (all must match), `"exact phrase"`, `a OR b`, `-exclude` / `NOT word`, `prefix*`. Optional when a filter is given |
| `mode` | `keyword`, `semantic` or `hybrid` (below) |
| `workspaceId`, `containerId` (list), `contentTypeId`, `termId` (with child terms), `createdBy`, `updatedFrom`, `updatedTo` | Filters |
| `$top` (max 100), `$skip` | Paging (`@odata.nextLink`) |

Each hit has:
- `snippet`: text around the match, from the passage that matched best;
- `page`: the page of the document that passage is on, or null for matches
  outside a page, such as the title or fields (SRC-09);
- `matchedBy`: `keyword`, `semantic` or both;
- `rank`: higher is better.

The response also returns `facets` (workspace, list, content type, term),
`@odata.count` and `mode`.

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
  each is on. The embedding job (`search.embeddings`, every minute) embeds new
  and changed passages in the background.
- Unchanged text is never embedded again.
- Changing the model embeds everything again with the new model. Until that is
  done, semantic results come only from the passages it has already embedded.
- After upgrading, run a reindex (`POST /v1.0/search/reindex` or
  `paperdotnet reindex`) so that existing content gets passages.

**Options** (`Search` section):

| Setting | Default | Meaning |
|---|---|---|
| `MinSimilarity` | 0.3 | Cosine similarity below which a passage is no match; tune it for your model |
| `CandidateLimit` | 200 | Documents per side before fusion |
| `EmbeddingBatchSize` | 64 | Passages per call to the model |
| `EmbeddingsPerRun` | 2000 | Passages per tenant and run |
| `EmbeddingSchedule` | `* * * * *` | Cron schedule of the embedding job |

**Limits:**
- Vectors are searched in memory on each server, on SQLite and PostgreSQL
  alike (ADR-0027). A tenant uses about passages × dimensions × 4 bytes, for
  example 50,000 passages × 768 dimensions ≈ 150 MB.
- A tenant's vectors are dropped from memory after 30 minutes without
  searches.
- The query text is sent to the configured model. Choose a local model when
  content must not leave your network.
