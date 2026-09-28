# 0009: Queries across lists load the full access of every list, one list at a time

- **Status:** confirmed
- **Area:** Lists
- **Date:** 2026-09-28

## Problem

My tasks, the calendar, the iCalendar export, smart folders and the task and
event reminder jobs query many lists. `IListItemStore.QueryAsync(lists, …)`
runs the single-list query for each list in turn. Each one loads the
workspace permission, the list, its access (the queries of
[0002](0002-uncached-permission-lookups-on-every-request.md) and
[0003](0003-permission-scope-preload-grows-with-list-size.md)), its content
types, and then the items. The results are merged and sorted in memory. The
cost grows with the number of lists the user can see, not with the page.

Measured with [the storage benchmark](../tests/benchmarks/item-storage/README.md)
(50 task lists of 2,000 tasks, only the access check and item query of each
list, so a lower bound): 93 ms on PostgreSQL and 135 ms on SQLite for My
tasks. One query over all 50 lists, with the assignee, status and due date
indexed, took 0.8 ms and 0.4 ms.

## Where it shows up

- [`ListItemStore.QueryAsync(IReadOnlyList<ListData>, …)`](../src/Modules/Lists/PaperDotNet.Lists/Features/ListItemStore.cs)
- [`TaskEndpoints` My tasks](../src/Modules/Tasks/PaperDotNet.Tasks/Features/TaskEndpoints.cs),
  [`CalendarService`](../src/Modules/Calendar/PaperDotNet.Calendar/Features/CalendarService.cs),
  [`SmartFolderQuery`](../src/Modules/Lists/PaperDotNet.Lists/Features/SmartFolderQuery.cs)
  (up to 100 lists)

## Possible approaches

- Compute the caller's allowed scopes once for the tenant and run one query
  over all the lists, with the shared fields (assignee, due date, status)
  indexed ([item-and-permission-storage.md](../docs/item-and-permission-storage.md)).
