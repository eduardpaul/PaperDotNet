# 0006: No concurrency ramp or large-dataset/permission-heavy performance baseline

- **Status:** possible
- **Area:** Other
- **Date:** 2026-09-27

## Problem

The only recorded performance data ([performance.md](../docs/performance.md))
is a one-caller-at-a-time smoke run against a 20-item list with no unique
permission scopes, on SQLite only. It explicitly says: no PostgreSQL numbers,
no concurrency ramp, and a list of only 20 rows. None of the permission- or
scale-sensitive paths raised in
[0002](0002-uncached-permission-lookups-on-every-request.md),
[0003](0003-permission-scope-preload-grows-with-list-size.md), and
[0004](0004-permission-change-fanout-is-a-large-inline-transaction.md) have
any measurement behind them; they are reasoned from reading the code, not
from observed numbers. Any decision to change them would currently be
speculative.

## Where it shows up

- `tests/PaperDotNet.Performance` already supports a concurrency ramp
  (`PERF_MAX_CONCURRENCY`) and a configurable item count (`PERF_ITEMS`), but
  the recorded runs never used them beyond the defaults, and never seeded
  lists with unique-permission folders/items or groups with many members.
- No PostgreSQL run has been recorded at all, so RLS overhead and SQLite's
  single-writer behavior under concurrent writes are both unmeasured.

## Possible approaches

- Run the existing ramp (`--` no `--smoke` flag) on both providers and record
  the results in `performance.md`, the same way the smoke run is documented.
- Add a performance scenario that seeds a list with many unique-permission
  folders and several groups, to get a number for
  [0002](0002-uncached-permission-lookups-on-every-request.md) and
  [0003](0003-permission-scope-preload-grows-with-list-size.md) before
  deciding whether to fix them.
- Add a scenario that breaks inheritance on a folder with a large descendant
  count under concurrent writers, to get a number for
  [0004](0004-permission-change-fanout-is-a-large-inline-transaction.md).

**This needs more investigation before scheduling:** this issue is itself the
investigation step for the others above; it should probably be done before,
or alongside, any of them rather than after.

## Update (2026-09-28)

[`tests/benchmarks/item-storage`](../tests/benchmarks/item-storage/README.md) now measures the SQL shapes at
scale on both providers: 1.6M items with 7,500 unique scopes and groups, a
concurrency ramp for the list-page request (pgbench, 1–16 clients), write
throughput with 8 writers, and the subtree rewrite with a concurrent writer.
It does not run the application, so the ramp in `PaperDotNet.Performance`
against the real API is still to be recorded.
