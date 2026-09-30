# Performance review: .NET 10, 2026-09-30

This is the initial assessment before the harness and memory fixes. The [baseline](performance-baseline-2026-09-30.md) and [memory investigation](memory-footprint-2026-09-30.md) document the subsequent implementation and measurements. Code observations below describe the earlier state.

## Decision

**Keep and improve the performance project. Invest first in reliable measurement, then in search and document-processing resource costs. A broad optimization rewrite is not justified.**

The repository already demonstrates that targeted architectural changes pay off: the recorded My tasks comparison improved eight-caller throughput from 46 to 367 requests/s and p95 from 238 to 31 ms. Removing permission-change reindex fan-out also improved unrelated endpoints by removing database competition. Those are historical measurements in [performance.md](performance.md), not measurements reproduced by this review.

Ordinary item reads and permission-filtered pages have encouraging historical and fresh exploratory latency. Search remains comparatively expensive and exceeded the runner's budget in this review. Server-only CPU efficiency, allocation rate, retained memory, native memory, and production capacity are still unknown. There is no evidence here that the application has a managed memory leak.

Review baseline: commit `3ad48fad8aa193165a4123bdcd8bf9536d3bfde5`; SDK 10.0.112 and runtime 10.0.12 on Ubuntu x64. Code inspection covers the host, performance runner, list querying/access, search, document intake/rendering, persistence registration, telemetry, and live events. This is a focused performance review, not exhaustive profiling of every module or the web UI.

## Fresh exploratory run

Release build passed with zero warnings/errors using `dotnet build tests/PaperDotNet.Performance -c Release --no-restore -m:1`. The initial default parallel build failed without useful diagnostics; limiting MSBuild to one worker completed successfully. No application code was changed.

```bash
PERF_ITEMS=200 PERF_SECONDS=3 PERF_MAX_CONCURRENCY=4 PERF_OUTPUT=/tmp/pdn-review-sqlite.json \
  /usr/bin/time -v -o /tmp/pdn-review-sqlite-resources.txt \
  dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll sqlite
```

Machine exposed 16 logical CPUs and 7.6 GiB RAM, with about 2.2 GiB available before the run and swap already in use. This is a shared development environment, not an isolated capacity worker. One run, no explicit warm-up, 3 seconds per step, callers 1/2/4. Ready time was 20.3 s, including seed/bootstrap. Fixture included 200 base items, 50 shared folders with 200 children, and 20 task lists. The create scenario then added 2009 successful items before the read scenarios; search readiness was not reverified after this work.

| Scenario | Requests/s at 4 callers | p50 ms | p95 ms | p99 ms | Counted errors |
|---|---:|---:|---:|---:|---:|
| Create | 339.9 | 5.0 | 26.2 | 134.7 | 0 |
| Read | 1105.4 | 2.4 | 9.0 | 11.0 | 0 |
| Filtered list | 1044.0 | 3.5 | 5.7 | 6.8 | 0 |
| Shared member page | 851.4 | 4.4 | 7.0 | 8.2 | 0 |
| My tasks | 267.7 | 14.0 | 22.3 | 28.3 | 0 |
| Keyword search | 3.2 | 927.1 | **1181.3** | 1181.5 | 0 |

Search completed only 9/12/10 successful requests at callers 1/2/4 respectively: p95 341/612/1181 ms. Its tail estimates are based on very few samples. It failed the configured 1000 ms p95 budget at four callers, but the process returned **exit code 0**, confirming the CI-gate weakness. This is an investigation trigger, not a statistically established capacity or comparison with earlier machines. The run does not isolate background indexing from search SQL work.

Whole-run `/usr/bin/time` measurements: 75.51 s elapsed, 175.54 s user CPU plus 19.73 s system CPU, 258% CPU (about 2.58 cores averaged), and peak RSS 759,528 KiB (**741.7 MiB**). These include application startup, migrations, fixture creation, indexing, load generation, all scenarios and shutdown. They cannot be attributed to a particular endpoint or used as server-only memory/CPU per request. No allocation or retained-heap capture was taken, and PostgreSQL was not rerun.

Logs also showed two transaction errors during create steps and a My tasks unordered row-limiting warning; the report counted no failed requests. Deadline cancellation can explain transaction errors, but the aggregate report alone cannot prove that attribution. The historical documentation's claim that a cancelled last request explains its reported p99 is also unsupported: cancelled requests never enter the percentile list. Separate cancellation/error telemetry is needed.

Raw [scenario JSON](performance-artifacts/2026-09-30/sqlite-results.json) and [process resource summary](performance-artifacts/2026-09-30/sqlite-resources.txt) are retained with this review.

## What the existing projects tell us

`tests/PaperDotNet.Performance` is a custom executable, not BenchmarkDotNet and not a `dotnet test` project. That is appropriate for its API scenarios. It starts the real application through `WebApplicationFactory`, uses temporary SQLite/WAL or PostgreSQL, authenticates real users, and measures create, point read, filtered query, shared-folder page, My tasks, and keyword search. Keep these scenarios and their provider coverage.

`tests/benchmarks/item-storage` is complementary: large SQL datasets, query shapes, write throughput, ACL layouts, field indexes, and an EF probe. It can isolate database design decisions that tiny API runs cannot. It cannot establish HTTP capacity or application memory consumption. Its scripts create/drop benchmark schemas and a probe database; run them only against a disposable benchmark database.

### Measurement weaknesses, in priority order

| Finding | Consequence | Improvement |
|---|---|---|
| Client and server share one process and thread pool | CPU, RSS and allocations include load generation, response copies, latency lists and the application | Keep this as an integration benchmark; add external traffic against a separate published Kestrel process |
| Default steps are 3 s; smoke steps are 1 s; no explicit warm-up or repeats | JIT/tiered optimization, EF compilation, cache misses and background activity can dominate | Warm up separately; measure 30–60 s per steady-state step initially; repeat at least three times |
| Workers wait for a response before sending another request | This measures closed-loop concurrency; offered load falls when the server slows | Retain concurrency tests; add externally scheduled arrival-rate tests and report dropped iterations |
| `create` runs first and retains every new item | Dataset size and indexing work vary by throughput and affect all following scenarios | Reset/reseed per scenario or explicitly label a mixed evolving workload and record row counts |
| Search is considered ready when **any** alpha hit appears, before shared/task seeding | The fixture can still have substantial indexing work pending | Verify expected indexed documents or backlog completion after all seeding; separately test indexing under load |
| Step deadline cancels active requests; these cancellations are excluded | Tail requests and completed writes with interrupted responses can disappear from statistics | Stop launching at deadline, drain with a grace period, report attempted/completed/failed/cancelled separately |
| Only successful request latencies enter percentiles; exceptions are reduced to one count | Fast failures and slow failures lack diagnosis; small p99 samples are unstable | Record status/error classes, timeout latency and sample count; retain success and all-attempt histograms |
| A failed budget still normally returns exit code zero | CI cannot use the result as a regression gate | Add an explicit gate mode with nonzero exit on required scenario/provider failures |
| PostgreSQL exceptions are broadly caught and can be skipped in `both` mode | An application bug can look like missing Docker; partial coverage can pass | Distinguish provisioning from scenario errors and record missing required coverage |
| Reports omit runtime, commit, hardware limits, duration, fixture size and background settings | Baselines cannot be compared confidently | Add metadata and per-step start/end resource snapshots plus time-series artifacts |
| Scheduler delayed by one hour, tiny default fixture, admin-heavy coverage | Maintenance, imports, OCR, tenant/user diversity and large ACL costs remain untested | Add separate production-like mixed and soak workloads |

The concurrency ladder only visits powers of two, so a cap of 12 tests through 8. `limitConcurrency` is the last passing tested step, not a discovered capacity limit; a passing maximum is only a lower bound. The 1000 ms default p95 budget is a configurable test policy, not a business SLO.

Harness maintenance: dispose the `JsonDocument` in `PostIdAsync` and the responses from inline seeding calls. The PostgreSQL path creates a role/database on an external server but does not drop them in disposal. Add cleanup guarded by ownership of that run's generated names, including failed initialization. Neither issue establishes an application leak, but both affect repeated testing.

## Where optimization is likely to pay

### 1. Search: strongest next API candidate

Evidence: [recorded PostgreSQL run](performance.md) after the reindex fix shows keyword search at 54 requests/s and p95 188 ms with eight callers, while shared pages reach 471 requests/s and p95 24 ms. Different work per endpoint means these are not directly interchangeable capacity numbers, but the gap warrants investigation.

Current `SearchService` has several concrete costs:

- `FilteredAsync` resolves readable scopes for the **whole tenant**, even when a workspace/container filter narrows the request. It materializes a scope dictionary, copies keys to an array, and sends that set back as a SQL parameter. Cost grows with the user's total accessible scopes. Compare appropriately narrowed scope resolution or a database ACL subquery; preserve authorization semantics.
- Keyword search executes an exact count, four sequential facet queries, a result page query, and potentially a passage query, in addition to access resolution. The public endpoint always constructs a request with facets enabled. A small `$top` does not bound count/facet work.
- `WithPassagesAsync` loads every matching passage for page document IDs, including passage text, and chooses the best passage in managed code. A page of 20 documents can still load many passages. Compare database-side best-passage selection on long documents.
- Page results materialize `SearchDocument` entities, including body text, before generating short snippets. Measure full body bytes loaded and compare narrower projections or database snippets.
- Ranking uses offset pagination, so deep pages need their own test; keyset pagination is more complex when ranks and the index change.

First experiment: fixed fully indexed fixture, keyword search with/without facets through service-level tests, query count and SQL execution time, large versus small scope sets, and 1/20/500-page documents. Capture PostgreSQL plans and SQLite `EXPLAIN QUERY PLAN`. If the UI does not need facets on every request, an opt-in API option could save substantial work. Exact count changes require an explicit API contract decision.

Do not run EF queries concurrently on the same DbContext to parallelize facets. Compare fewer queries, shared database work, or separate contexts only after profiling.

**Semantic search is a separate, potentially larger scaling cost that the current runner never exercises.** `VectorIndex` holds a tenant's embeddings in memory and `VectorSnapshot.Nearest` performs a brute-force SIMD dot product over matching entries. Query cost is proportional to matching passages × vector dimensions. At 100,000 passages and 1536 float dimensions, raw vectors alone occupy about 586 MiB per active tenant, excluding object/index overhead and refresh buffers. This is a calculated sizing example, not the application's configured model or a measurement.

Every semantic query also acquires a per-tenant semaphore to check count/newest embedding state in the database. Cold loading and refresh happen under that semaphore. Refresh materializes changed embedding bytes and converts them to float arrays, builds a dictionary and a new snapshot; peak memory can exceed steady-state vector storage. Sliding expiration drops inactive snapshots after 30 minutes, but this code sets no explicit byte capacity and the per-tenant `_locks` dictionary is never pruned. Lock entries therefore accumulate with tenants encountered; their significance depends on tenant churn and is unmeasured.

Benchmark 10k/100k passages, real model dimensions, multiple active tenants, cold refresh, embedding updates and concurrent semantic requests. Include security trimming: candidate retrieval occurs before SQL authorization filtering, which can reduce returned results for restricted users. If semantic search is a deployment requirement, this becomes a high-priority CPU/memory investigation. Compare bounded cache/admission, refresh scheduling, and an approximate-nearest-neighbor backend only after documenting acceptable retrieval quality and permission behavior. Do not replace a simple vector implementation merely because a vector database exists.

### 2. Document processing: strongest memory and native CPU candidate

The normal upload path is already sensibly streamed: `FileIntake.SpoolAsync` hashes into a temporary file with a bounded buffer; `LocalBlobStore` copies streams; content and stored renders are reused. Preserve this design.

Rendering is different. `PageRenderer.RenderPdfPageAsync` copies the complete PDF into a `MemoryStream`, copies it again with `ToArray`, and only then waits for the global PDFium semaphore. Multiple render calls can retain these PDF buffers while queued. `RenderForOcrAsync` also reads the entire PDF and acquires the same semaphore per page. The semaphore protects non-thread-safe PDFium but does not bound memory admitted before the lock.

At the configured 100 MiB file limit, a single rendering call can transiently hold roughly two PDF-sized managed buffers, plus JPEG output and native rendering memory. Stream growth can increase that further. This is an order-of-magnitude estimate from code, not a measured peak. Large decoded images can also greatly exceed compressed file size. Defaults allow 500 OCR pages at 300 DPI; file-byte and page-count caps do not describe the complete memory budget.

High-value experiments: concurrent render requests on large PDFs, repeated pages/widths from one document, huge-dimension JPEG/PNG files, and API traffic during OCR. Measure process RSS, container total memory, LOH, GC pauses, semaphore waiting and OCR throughput. External Tesseract/OCR processes and native Skia/PDFium allocations need OS/container metrics; managed GC data alone misses them.

Candidates after measurement: admit bounded render jobs before buffering, reuse a document session where the library permits, use stream/file APIs where supported, avoid repeated full-file copies, and isolate native render/OCR workers if their resource spikes damage API latency. Preserve PDFium serialization unless its documented thread-safety contract changes. Adding parallelism can increase memory and queue contention instead of throughput.

MCP base64 uploads and GLM OCR also materialize byte arrays/base64 strings. Include them when used: their memory profile differs from multipart streaming.

### 3. List query parsing and response allocation: plausible, lower priority

`ItemQueryRunner.Parse` rebuilds the EDM model and alias dictionary and instantiates an OData parser for each call. `RunAsync` parses the view and request, then reparses a request filter; hierarchy expansion can add more parsing. This is a concrete CPU/allocation candidate, but its share of total request cost is unmeasured.

`ItemResponse.From` parses the complete JSON field object, creates a temporary property list and deep-clones values into another object. `$select` removes unwanted fields only after that work. Large field payloads and 100–1000-row responses deserve allocation benchmarks even if tiny title-only pages are fast.

Use BenchmarkDotNet for EDM construction, representative OData filters, response conversion with field-size/select parameters, and related pure transformations. Cache immutable EDM metadata only with a bounded cache keyed by tenant/schema version and field type registry semantics. Do not cache user/time-specific aliases, request translators, DbContexts, or access decisions accidentally. Optimize repeated parsing and cloning only if traces show useful savings.

The code already uses no-tracking reads, bounded pages, indexed field promotion, ACL lookup by principal, grouped cross-list queries and keyset paging for supported orders. These should be preserved. Custom orders that fall back to offset paging and unindexed substring filters still need large-data tests.

### 4. Live events and long-lived clients: load-dependent

`LiveEventHub.Deliver` scans every subscriber for each event. Its 256-entry per-client channels bound event count and protect publishers from slow readers, which is good. They do not impose a byte budget, and there is no subscriber-count bound here. Audience filtering occurs in the SSE stream (`Jobs/Features/Operations.cs`), after delivery to tenant subscribers. Consequently unrelated tenant subscribers can still receive queue work even when the event is never sent over their socket.

Test 100/1000/5000 clients, event bursts, large event payloads, slow readers, reconnects and tenant diversity. Track active clients, dropped events, delivery CPU and retained memory. If material, partition subscribers by tenant/user and consider earlier audience filtering with reliable principal refresh. Current filtering must remain correct after permission changes. There is no basis for a leak claim: subscriptions remove themselves in `finally`.

### 5. Persistence, caching and startup: validate, avoid speculative rewrites

Module DbContexts use `AddDbContext`, not pooling. Pooling can reduce setup/allocation costs, but these contexts depend on tenant state and interceptors; safe state reset is essential. Try it only after context construction appears in profiles. Npgsql connection pooling and DbContext pooling are separate concerns. See [EF Core performance guidance](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics).

Principal IDs already use a tenant/user-keyed HybridCache with invalidation tags. Allowed scopes remain queried. Do not add broad authorization result caching without testing revocation and multi-instance invalidation. Test warm/cold cache behavior and many distinct users, rather than one administrator.

The former PostgreSQL tenant-setting round trip was already addressed; [issue 0007](../issues/0007-postgresql-rls-session-overhead-under-pooling.md) records the implementation and improvement. Testing behind PgBouncer remains outstanding. Do not present the old per-open interceptor cost as an unfixed issue.

ReadyToRun is enabled for RID publishing; configuration binding generation is enabled. In-process dev/test builds do not represent that published startup. Native AOT/trimming would require compatibility work on the current stack, so they are poor first performance investments. Separate process startup, migrations/bootstrap and steady-state throughput before changing compilation settings.

## Modern .NET 10 measurement stack

| Question | Tool/method | What to collect |
|---|---|---|
| Can deployed HTTP traffic meet its SLO? | External k6 against published Kestrel; concurrency and arrival-rate workloads | Offered/achieved rate, p50/p95/p99, errors, dropped iterations, payload bytes |
| How much CPU/memory does the server use? | `dotnet-counters`, existing OpenTelemetry runtime/ASP.NET metrics, OS/cgroup metrics | CPU time, RSS, GC heap, allocation rate, collection/pause time, contention, thread-pool queue, active requests |
| Which managed work consumes time/allocations? | `dotnet-trace collect`, separately sampled allocation/GC traces | Managed stacks, GC/allocation samples, database events; distinguish waits from CPU |
| Where does native CPU/memory go? | .NET 10 `dotnet-trace collect-linux`, Linux tools, process/container measurements | Native/kernel stacks, on-CPU samples, context switches, child-process resource use |
| What remains alive after sustained traffic? | `dotnet-gcdump`, `dotnet-dump`, heap comparison after equivalent workload phases | Retained types, roots, native-versus-managed growth |
| Does a small code change improve efficiency? | BenchmarkDotNet `MemoryDiagnoser`, appropriate threading/profiling diagnosers | Stable timing distributions, bytes/op, collections, contention |
| Is the bottleneck database work? | PostgreSQL `pg_stat_statements` and plans; SQLite plans and statement timings | Query count, execution time, rows/buffers, lock waits, pool waits, WAL/write effects |

The application already configures OpenTelemetry runtime, ASP.NET Core, HTTP-client and Npgsql instrumentation. Set an OTLP endpoint to export it. Extend it with indexing/workflow backlog, oldest pending age, render queue wait, active native jobs, cache hit/miss, and SSE drop/client metrics. SQLite and document processing require their own attribution; Npgsql spans do not cover them.

.NET runtime metrics expose CPU, allocation and GC signals; allocation rate is derived from the cumulative allocated-byte metric. Compare CPU seconds and allocated bytes per completed request under equivalent workloads, alongside absolute memory peaks. For mixed background activity these are service-level costs, not isolated endpoint allocations. See [runtime metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime) and [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters).

Current tracing distinguishes portable sampled thread time from Linux kernel CPU sampling. The installed 10.0.745401 tool confirms `dotnet-sampled-thread-time` for `collect`, and `cpu-sampling`/`thread-time` for `collect-linux`. Do not label sampled wall-clock stacks as a precise on-CPU profile. Linux collection requires root and a supported kernel. See [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).

Heap dumps are investigative runs, not neutral latency measurements: `dotnet-gcdump` induces a full GC and needs additional resources. Compare snapshots separately from baseline throughput. See [dotnet-gcdump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump). BenchmarkDotNet memory diagnosers are useful for isolated operations; they do not replace server RSS or retained-heap testing ([diagnosers](https://benchmarkdotnet.org/articles/configs/diagnosers.html)).

k6 arrival-rate executors schedule starts independently of response completion and expose insufficient generator capacity through dropped iterations. Provision the generator separately and check its CPU before attributing missed load to the server. See [open versus closed workloads](https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/) and [constant arrival rate](https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/constant-arrival-rate/).

Example collection commands for a **separate server PID**, after fixture preparation and warm-up:

```bash
dotnet-counters collect --process-id SERVER_PID --counters System.Runtime,Microsoft.AspNetCore.Hosting --format json --output counters.json
dotnet-trace collect --process-id SERVER_PID --profile dotnet-common,dotnet-sampled-thread-time,database --duration 00:00:00:30 --output request.nettrace
dotnet-trace collect --process-id SERVER_PID --profile gc-verbose --duration 00:00:00:30 --output allocations.nettrace
dotnet-gcdump collect --process-id SERVER_PID --output retained.gcdump
```

Replace `SERVER_PID` with the numeric PID. `dotnet-counters` and `dotnet-gcdump` are not installed in the inspected global tool list. Pin diagnostic tool versions in a local manifest for repeatability. Run clean baseline and profiling passes separately to quantify instrumentation overhead.

## Proposed experiment plan and acceptance criteria

1. **Make the existing harness trustworthy.** Metadata, warm-up, repeats, fixture/index readiness, failure classification, deadline draining and an explicit CI gate. Keep a quick smoke mode for fixture correctness; use longer runs for performance claims.
2. **Establish separate-process baselines on both providers.** Published Release build, representative production configuration, durable PostgreSQL settings, fixed CPU/memory limits and database version. Begin with 200/2000 items, then representative 100k+ datasets; use the million-row SQL suite for storage-shape questions. Include non-admin users and many tenants.
3. **Profile search and rendering.** These have the strongest combined code/historical evidence. Optimize one hypothesis at a time and compare the same fixture, offered load and resources.
4. **Run mixed traffic and a soak.** Reads/search/writes while indexing, OCR and maintenance run; 30–60-minute initial soak, then longer runs if retained memory/backlog continues growing. Include SSE clients. Record disk/temp growth and child-process CPU/RSS.
5. **Promote stable checks into CI.** Smoke/functional workload validation on ordinary CI; repeated regression baselines on a controlled worker. Store raw JSON, time series, trace summaries, commit/runtime metadata and fixtures. No hard throughput gate based on one run on a busy workstation.

Agree on deployment demand before declaring performance inadequate. Illustrative starting targets, not measured promises: interactive list p95 <100 ms, search p95 <300 ms, errors <0.1%, stable pending-work age, and peak total service memory below 80% of its deployment limit at expected traffic. Choose targets based on the actual user experience and hosting budget.

An optimization is worthwhile when it fixes an SLO/resource-limit breach, avoids documented scaling cost, or produces a reproducible material improvement with acceptable complexity. A provisional comparison rule is at least 15–20% lower CPU/request or allocation/request, or meaningful p95/peak-memory improvement across repeats, with no authorization/correctness regression. This is a decision threshold, not statistical proof; compare variation and sample size as well.

**Recommended allocation of effort:** measurement first; search query/materialization work second; rendering admission and memory third. Parser/JSON micro-optimizations, DbContext pooling, and compilation changes follow only if measured profiles justify them. Existing ordinary CRUD performance does not support a blanket rewrite.
