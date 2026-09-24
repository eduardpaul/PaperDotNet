# ADR-0012: Full-text search in the database (FTS5 / tsvector) with principal trimming

**Status:** Accepted (2026-09-24)

## Context
SRC-01…04 need one search across all data types with phrases, OR, NOT and
prefixes, filters with facets, and strict security trimming. The minimal
install is one container with SQLite (ADR-0009); PostgreSQL is optional;
external engines must stay optional.

## Decision
- **Search module** with one `search.documents` table (+ `document_principals`,
  `document_tags`). Owning modules push documents through
  `ISearchIndex` (Search.Contracts) from their integration events, so indexing
  is asynchronous and never part of the write transaction. Lists indexes items
  (title, text fields, term labels and synonyms) and re-indexes a whole list
  when it is deleted or its permissions change (`ListIndexInvalidated`).
- **Full-text engine per provider** behind `IFullTextSearch` (Persistence):
  - PostgreSQL: stored generated `tsvector` (`simple` configuration, title
    weight A, body B) + GIN index, `to_tsquery`, `ts_rank`.
  - SQLite: FTS5 external-content table `{table}_fts` with sync triggers
    (created by the migration via `SqliteFullTextSearch.CreateIndexSql`),
    `MATCH`, `bm25` (title weighted 10:1).
  - Both split words on punctuation the same way (PostgreSQL text is
    normalized with `regexp_replace` first).
- **Query syntax** is parsed once (`FullTextQuery`: words, `"phrases"`, `OR`,
  `-word`/`NOT`, `prefix*`) and rendered per provider, so user input never
  reaches the engine unescaped.
- **Security trimming:** each document stores the principals that may read it
  (`u:`, `g:`, workspace member `w:`, workspace owner `o:`), computed with the
  same rules as ADR-0011. A query only returns documents sharing a principal
  with the caller (an indexed `EXISTS`).
- `POST /v1.0/search/reindex` rebuilds a tenant's index as an operation.

## Consequences
- Works in the one-container install; no extra service.
- No stemming or language awareness yet (SRC-05, P3): `simple` config and
  `unicode61` tokenizer.
- Search results are eventually consistent (seconds).
- A SQLite migration that rebuilds `search_documents` must re-create the FTS
  table and triggers (`DropIndexSql`/`CreateIndexSql`).
