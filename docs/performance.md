# Performance

One Release smoke run of `tests/PaperDotNet.Performance` on SQLite. PostgreSQL was not measured, and concurrency never went above one caller. This is a baseline, not a capacity limit.

## How it was run

- Binary: `tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll --smoke sqlite`
- In-process API (`WebApplicationFactory`), not a socket and not a reverse proxy. Numbers exclude network and TLS.
- Fresh SQLite file, WAL, the same pragmas as a normal install. Jobs were held off for an hour so the scheduler did not run during the sample.
- 20 items in one list (`alpha note N` / `beta note N`), then about 1 second per scenario at one caller. The p95 budget was 1000 ms. A step "holds" when it has no failed responses and p95 stays under that budget.
- Machine: WSL, 16 logical CPUs, 7.6 GiB RAM. The editor's language service was running. Treat the absolute rates as this machine, not a server.

Startup, including migrations, the first administrator, and seeding those 20 items, took 10.0 s. Keyword search could see `alpha` 2.3 s after the seed finished.

## Results

| Scenario | Requests in 1 s | req/s | p50 | p95 | p99 | Errors |
|---|---:|---:|---:|---:|---:|---:|
| Read one item | 208 | 208 | 4.4 ms | 6.8 ms | 12 ms | 0 |
| Filtered list (`contains(fields/title,'alpha')`, `$top=20`) | 131 | 131 | 5.6 ms | 8.7 ms | 11 ms | 0 |
| Create an item | 70 | 69 | 10 ms | 39 ms | 59 ms | 0 |
| Keyword search (`q=alpha` in that workspace) | 58 | 58 | 17 ms | 20 ms | 21 ms | 0 |

Create is the expensive call. It validates fields, writes the item, and publishes the change that search indexes. A point read is about three times that rate, and the filtered page is in between. Search is the slowest read: one query is about 17 ms even on a 20-item index, and it does not get faster just because the list is small.

During the create second, EF logged `An error occurred using a transaction` once. The runner still counted zero failed responses. That line is the in-flight create cancelled when the one-second window closed, not a rejected write. The same cutoff is why p99 on create (59 ms) sits well above p50 (10 ms): the last request is interrupted.

`limitConcurrency: 1` in the JSON means the smoke cap was one caller. It does not mean a second caller fails.

## Permission-heavy ramp (2026-09-29)

After [ADR-0035](adr/0035-item-storage-and-permissions-at-scale.md) step 1 (the ACL looked up by principal), with the
`shared` scenario: a workspace member without full control pages (`$top=20`) a list of 500 folders with unique
permissions (owners only), half of them shared with a group the member is in, 2,000 documents in them. Every request
resolves the member's principals (cached) and allowed scopes (one index lookup). The other scenarios run as the
administrator (full control, no permission filter).

- `PERF_ITEMS=2000 PERF_MAX_CONCURRENCY=8`, three seconds per step, Release, in-process.
- Cloud container, 4 cores. PostgreSQL 16 on the same machine (`fsync=off`), one provider at a time.

| Scenario, 8 callers | SQLite req/s | p95 | PostgreSQL req/s | p95 |
|---|---:|---:|---:|---:|
| Read one item (admin) | 1,257 | 14 ms | 362 | 68 ms |
| Filtered list (admin) | 717 | 21 ms | 260 | 93 ms |
| **Member's page of the shared list** | **451** | **31 ms** | **210** | **102 ms** |
| Keyword search (admin) | 3.3 at 1 caller | 398 ms | 34 | 315 ms |

The member's page holds 8 callers on both databases with no errors. Before step 1 each of these requests loaded all
500 unique scopes of the list, and the storage benchmark measured that load at 84–94 ms (PostgreSQL) and 260 ms (SQLite)
with 7,500 scopes ([issue 0003](../issues/0003-permission-scope-preload-grows-with-list-size.md)).

Not about permissions, seen in the same run:

- Creates were slow right after seeding (SQLite p95 about 1 s at one caller, PostgreSQL 1.4 s at two). About 750 grant
  changes during the seed each queued a full reindex of the list, and those were still running; on PostgreSQL they
  raced on `search.document_principals` ([issue 0008](../issues/0008-permission-change-rebuilds-list-search-index.md)).
- SQLite search stayed at a few requests per second over 2,200 documents while that reindexing ran.
- The API allows 1,200 requests a minute per user by default. The runner sets `RateLimit:PermitPerMinute` higher so it
  measures the server, not the limit.

### After step 3 (fan-out), PostgreSQL

The same run on PostgreSQL after [ADR-0035](adr/0035-item-storage-and-permissions-at-scale.md) step 3 (grant changes
no longer reindex the list; search trims by scope). The seed's 750 grant changes queued no reindexing, so the server
log had no duplicate-key errors or deadlocks (2,526 and 18 before), and the ready time fell from 163 s to 98 s.

| Scenario, 8 callers | Before step 3 req/s | p95 | After req/s | p95 |
|---|---:|---:|---:|---:|
| Create an item (admin) | held 1 caller | 168 ms at 1 | 339 | 38 ms |
| Read one item (admin) | 362 | 68 ms | 614 | 21 ms |
| Filtered list (admin) | 260 | 93 ms | 430 | 28 ms |
| **Member's page of the shared list** | **210** | **102 ms** | **471** | **24 ms** |
| Keyword search (admin) | 34 | 315 ms | 54 | 188 ms |

Most of the gain is the missing reindex backlog competing for the database, not a faster query path; one run each,
same machine.

### Queries across lists (step 5), SQLite

`PERF_ITEMS=400 PERF_MAX_CONCURRENCY=8`: a member reads My tasks (`/v1.0/me/tasks`) over 20 task lists of 10 tasks,
half assigned to them. The same runner on the commit before step 5 and after it:

| Callers | Before req/s | p95 | After req/s | p95 |
|---:|---:|---:|---:|---:|
| 1 | 18 | 108 ms | 106 | 13 ms |
| 2 | 30 | 122 ms | 218 | 13 ms |
| 4 | 42 | 155 ms | 371 | 17 ms |
| 8 | 46 | 238 ms | 367 | 31 ms |

Before, each list loaded its own schema and access and ran its own query; now the 20 lists are one group and one
query.

## Build-time compilation

Native AOT and trimming were not turned on. Wolverine, the EF JSON translators and the event dispatcher bind handlers with reflection (`MakeGenericMethod` and `GetMethod`), and one binary contains both SQLite and PostgreSQL. Trimming or AOT drops that code and the process fails at runtime. The Minimal API request-delegate generator was tried and turned back off: for several `Results<…>` endpoints it emits source that does not compile.

What is on:

- **Configuration binding source generator**, for every project (`EnableConfigurationBindingGenerator`). `BindConfiguration` is compiled instead of reflecting over the options type. The generated binder is in the Release host binary.
- **ReadyToRun** when publishing with a runtime identifier. The image build publishes framework-dependent `linux-x64` (`--self-contained false`), so `dotnet paperdotnet.dll` still works and Crossgen2 has already compiled the IL. Dev builds and this smoke runner do not go through that publish, so the table below does not include ReadyToRun.

The same SQLite smoke, after the binding generator was enabled:

| Scenario | req/s | p50 | p95 | Compared with the first smoke |
|---|---:|---:|---:|---|
| Read one item | 178 | 4.9 ms | 12 ms | slower (was 208 req/s, p95 6.8 ms) |
| Filtered list | 144 | 5.3 ms | 7.4 ms | about the same (was 131 req/s, p95 8.7 ms) |
| Create an item | 103 | 6.9 ms | 20 ms | faster (was 69 req/s, p95 39 ms) |
| Keyword search | 47 | 21 ms | 23 ms | slower (was 58 req/s, p95 20 ms) |

Ready time was 9.5 s (was 10.0 s). Search saw `alpha` after 2.1 s (was 2.3 s). Two in-flight creates were cancelled at the end of the one-second window and logged as transaction errors; neither was a failed response.

That is not a speedup. The binding generator does not run on these requests, and a one-second sample on a busy machine moves by this much on its own. Create looking faster and search looking slower is noise. ReadyToRun would show up as a shorter cold start of the published process, which this in-process runner does not measure.

## What this does not say

- No PostgreSQL numbers. SQLite's single writer will show up only when several creates run together.
- No concurrency ramp. One caller left a lot of CPU unused, so these rates are latency of the API plus the database, not a saturated throughput.
- The list had 20 rows. A filtered query and a search over thousands of items will be slower. The index lag of 2.3 s is for 20 items.
- In-process calls skip Kestrel's socket path. A deployed process will be a bit slower per call and will spend extra time in TLS and the proxy.

For storage and permission queries at scale (1.6M items, thousands of unique scopes, both providers, a concurrency ramp), see [`tests/benchmarks/item-storage`](../tests/benchmarks/item-storage/README.md) and [item-and-permission-storage.md](item-and-permission-storage.md). That benchmark runs SQL directly, without the application.

## Running it again

Smoke, SQLite only:

```bash
dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll --smoke sqlite
```

A real limit check (still one provider at a time) raises concurrency from 1 through 16, three seconds per step, 200 items, and stops when any response fails or p95 crosses 1000 ms:

```bash
dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll sqlite
dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll postgresql
```

PostgreSQL needs Docker, or `PAPERDOTNET_TEST_POSTGRES` pointing at a server the runner may create a database on. Do not run both providers, or a rebuild, while the machine is already short of memory. `PERF_ITEMS`, `PERF_SECONDS`, `PERF_MAX_CONCURRENCY`, and `PERF_P95_MS` change the run. Results go to `perf-results.json` unless `PERF_OUTPUT` is set.
