# 0013: Full-text search across all data types, plus vector search

- **Status:** mapped
- **Area:** Search / Platform
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): SRC-01…10, AI-05

## The idea

One search that covers **every data type** in a tenant:

- documents, including their OCR text
- tasks, events and notes
- list items of any content type, including those added by extensions
- tags/terms (idea 0008) and people

It combines two kinds of search:

- **Full-text search:** keywords, phrases, `OR`/`NOT`, and filters on
  fields, tags, content type, owner and dates.
- **Vector (semantic) search:** find by meaning, e.g. "contracts about
  late-payment penalties" matches documents that never use those words.

Also **hybrid ranking** that mixes both kinds of result.

## Why / problem it solves

- Users shouldn't need to know where something lives or what type it is.
- Scanned documents often have poor OCR or different wording, and semantic
  search finds them anyway.
- The same index is the retrieval layer for AI features: Q&A over your
  documents, MCP tools (idea 0004), and auto-tagging (ideas 0008, 0009).

## Examples / references

- SharePoint / Microsoft Search: one search box across sites, lists and
  libraries, with managed properties and refiners (facets).
- Papermerge: PostgreSQL full-text search over OCR text, with tag, category,
  owner and custom-field filters
  (see [papermerge-features.md](../docs/papermerge-features.md) section 6).
- Building blocks ([dotnet-building-blocks.md](../docs/dotnet-building-blocks.md)):
  - PostgreSQL `tsvector` full-text search
  - pgvector through `Microsoft.Extensions.VectorData`
  - `Microsoft.Extensions.AI` `IEmbeddingGenerator`
  - `Microsoft.Extensions.DataIngestion` for chunking
  - all behind the `ISearchProvider` abstraction

## Notes

<!-- Open questions to settle during review:
     - Self-hosting (guiding principle): the default is PostgreSQL only, with
       tsvector for full-text and the pgvector extension for vectors. No extra
       service needed. External engines (OpenSearch, Meilisearch, Qdrant) are
       optional providers.
     - Vector search requires an embedding model. Options:
         - off by default
         - a local model (ONNX Runtime in-process, or Ollama)
         - a cloud AI provider, configured per tenant
       Should full-text search always work without any AI configured?
     - One unified search table/index (item id, tenant, content type,
       text, tsvector, embedding, ACL info) fed by the event outbox. Or
       per-module indexes merged at query time?
     - What gets indexed per content type:
         - which fields
         - weights (title > body > OCR text)
         - per-language stemming
       Extensions declare this for their content types (a search
       extension point).
     - Chunking long documents: embeddings per chunk, with page numbers kept
       so results can point to "page 7".
     - Hybrid ranking: e.g. reciprocal rank fusion of full-text and vector
       scores. Tunable per tenant?
     - Security trimming: results must respect item permissions and sharing
       (not just tenant). Filter in SQL with ACL data, or post-filter?
     - Facets and refiners: content type, tags (hierarchical, idea 0008),
       owner, dates, custom fields.
     - Reindexing: when schema, language or embedding model changes, as a
       background job with progress.
     - API: Graph-style `/search/query` endpoint (idea 0003) and an MCP search
       tool (idea 0004).
     - Cost and size: embedding storage per tenant, quotas (idea 0005). -->
