# 0001: List pages get slow as a folder grows

- **Status:** possible
- **Area:** Lists
- **Date:** 2026-09-26

## Problem

Opening a list and scrolling it costs more as one folder gets large. The page
size is not the cause. Each page sorts the folder's rows, and each page counts
them again. The smoke run in [performance.md](../docs/performance.md) measured
a filtered page of 20 items (about 6–9 ms). It does not describe a folder of
thousands.

## Where it shows up

The list screen (`web/src/routes/_app/w/$workspaceId/l/$listId/index.tsx`) loads
100 rows through `itemsQuery` and follows `@odata.nextLink`. The first request
sends `$count=true`. The screen reads that total from the first page only
(`odataCount`). The next link is built by copying every query parameter except
`$skiptoken` (`ItemQueryRunner.NextLink`), so `$count=true` stays on every
later page and each scroll counts the folder again.

The same request always sends an order when the list allows folders: folders
first (`isFolder desc`), then the column sort or the view's `orderBy`. The
items API uses a keyset (`Id` after the last row) only when there is no
`$orderby`. This screen sends one, so the page is `ORDER BY … OFFSET n LIMIT
100`. Page 30 has walked 3,000 rows. Sorting `fields/dueDate` or another JSON
field cannot use the indexes on `items`: `(ListId, ParentId)` and
`(ListId, ScopeId)`. `Title`, `CreatedAt`, and `UpdatedAt` are real columns;
other fields are read out of the JSON document.

The folder is already the working set. The screen filters `parentId eq null`
or `parentId eq {folder}` unless the user is searching. A large root, or one
large folder, is the slow case. The search box adds
`contains(tolower(fields/title), …)`, which reads the JSON and does not use
the `Title` column.

MCP `query_items` / `list_children` are a separate path. With no `orderBy`
they keyset on `Id` and do not count. They do not make the screen faster.
Their cursor is a second copy of the items-endpoint cursor (`ListPageAsync`
next to `ItemQueryRunner.RunAsync`).

## Possible approaches

1. **Count once.** Do not copy `$count` onto the next link. The screen already
   shows the total from the first page. Later pages stop scanning the folder
   just to count it.
2. **Index the order the screen uses.** `(ListId, ParentId, IsFolder, Title, Id)`
   matches folders-first then title and can stop after 100 rows. The same
   shape covers `createdAt` and `updatedAt`. A due date or a status needs a
   real column or an expression index, one per database
   (`Persistence.Sqlite` / `Persistence.PostgreSql`). Unusual sorts can keep
   today's offset.
3. **Continue from the last row.** After that index exists, the next page is
   "after this folder flag, this title, and this id," carried in `$skiptoken`.
   Offset sorts the folder again and discards the earlier rows. The unordered
   `Id` keyset does not help this screen, because the screen always orders.
4. **Search the title column.** Point the list search box at `Title` with a
   case-insensitive index, instead of `contains` on the JSON.

A stored child count is only useful if the total must stay exact and counting
once is not enough. A filtered view (for example status not completed) still
has to count its own rows.

## Measured (2026-09-28)

[The storage benchmark](../tests/benchmarks/item-storage/README.md) measured the JSON sort part only: one stage
of 500,000 deals sorted by a JSON date took 243 ms on PostgreSQL and 480 ms on
SQLite, and 2.9 ms and 1.5 ms from indexed columns. Paging and repeated counts
were not measured. Options: [item-and-permission-storage.md](../docs/item-and-permission-storage.md),
decision 2.
