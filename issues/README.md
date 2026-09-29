# Issues

Possible problems worth fixing later. An issue is a known gap or cost, not a
feature idea. Ideas stay in [`ideas/`](../ideas/README.md).

## How to add an issue

1. Copy [`_template.md`](_template.md) to `NNNN-short-title.md`. Use the next
   free number.
2. Fill in the problem and where it shows up. Possible approaches are optional.
3. Add a row to the index below.

## Status

| Status | Meaning |
|---|---|
| `possible` | Recorded, not scheduled |
| `confirmed` | Reproduced with a measurement or a failing case |
| `doing` | Being fixed |
| `done` | Fixed (note the change) |
| `wontfix` | Left as is (reason noted in the file) |

## Index

| # | Title | Area | Status |
|---|---|---|---|
| [0001](0001-list-pages-slow-as-a-folder-grows.md) | List pages get slow as a folder grows | Lists | possible |
| [0002](0002-uncached-permission-lookups-on-every-request.md) | Permission and membership lookups are uncached on every list/item request | Lists | done |
| [0003](0003-permission-scope-preload-grows-with-list-size.md) | Loading a user's permission scopes preloads every unique scope in the list | Lists | done |
| [0004](0004-permission-change-fanout-is-a-large-inline-transaction.md) | Breaking or resetting inheritance on a large folder is one large inline transaction | Lists | confirmed |
| [0005](0005-dynamic-json-fields-no-promotion-path-for-hot-columns.md) | No promotion path from a hot dynamic field to a real indexed column | Lists | confirmed |
| [0006](0006-no-concurrency-or-large-dataset-performance-baseline.md) | No concurrency ramp or large-dataset/permission-heavy performance baseline | Other | possible |
| [0007](0007-postgresql-rls-session-overhead-under-pooling.md) | PostgreSQL RLS tenant-setting overhead under connection pooling is unmeasured | Other | confirmed |
| [0008](0008-permission-change-rebuilds-list-search-index.md) | A permission change rebuilds the list's whole search index and drops its embeddings | Search | possible |
| [0009](0009-cross-list-queries-load-access-per-list.md) | Queries across lists load the full access of every list, one list at a time | Lists | confirmed |
| [0010](0010-sqlite-field-filters-parse-json-per-row.md) | On SQLite, every equality filter on a field parses the JSON of every row | Lists | confirmed |
| [0011](0011-item-live-events-reach-every-tenant-user.md) | Item change events reach every user of the tenant | API | possible |
| [0012](0012-permission-change-forces-full-delta-resync.md) | Any permission change makes every delta client of the list sync it again | API | possible |
