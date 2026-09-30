# Separate-process performance baseline, 2026-09-30

The performance harness now launches a published Production Kestrel application
and measures server CPU/RSS separately from the HTTP load generator. Its
[measurement contract and commands](../tests/PaperDotNet.Performance/README.md)
describe warm-up, repeated fixtures, index convergence, deadline draining,
failure classification and CI gates. This supersedes the old runner's measurement
method for future comparisons; old numbers remain historical evidence.

## Profile

```bash
DOTNET_PROCESSOR_COUNT=4 TESTCONTAINERS_RYUK_DISABLED=true \
PERF_HOST_PATH=/tmp/pdn-performance-host/paperdotnet.dll \
PERF_ITEMS=200 PERF_SECONDS=10 PERF_WARMUP_SECONDS=2 \
PERF_MAX_CONCURRENCY=2 PERF_REPEATS=3 \
PERF_OUTPUT=artifacts/performance-baseline-2026-09-30/results.json \
dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll both --gate
```

The host was published with `dotnet publish src/PaperDotNet.Host -c Release
--no-restore -m:1 -o /tmp/pdn-performance-host`, without a RID. It uses normal JIT
and server GC, not ReadyToRun. Runtime 10.0.12 / SDK 10.0.112, Ubuntu 26.04 under
WSL2, x64; 16 machine logical CPUs, 7.6 GiB RAM and 2 GiB swap. Four runtime
processors were exposed to both processes. That setting sizes runtime resources
and **does not impose a CPU quota or memory cap**. The shared development machine
already used swap before testing. Providers and repeats ran sequentially.

Each repeat started a fresh database, seeded 200 base items, 50 permission-scoped
folders with 200 child items, and 100 tasks across 20 task lists. All **500**
non-folder documents had to appear in the search index, with the expected 100
alpha hits, before reading scenarios started. Five read scenarios precede create.
Every step warms up for two seconds then admits requests for ten seconds at one
or two callers. In-flight requests drain; request/deadline failures remain in the
statistics. This is an initial repeated workstation baseline, not a saturated
capacity test or a tightly controlled regression worker. Longer runs default to
30-second steps in the harness.

SQLite uses a temporary local WAL database and the app's `synchronous=NORMAL`.
PostgreSQL uses a fresh Docker `postgres:17-alpine` container per repeat, with
version and all three durability settings verified in each report. The local
Docker resource reaper failed before PostgreSQL startup; this run explicitly
disabled it and relied on verified normal disposal. This workaround is not a
harness default. Container/image download time is not part of measured steps.

Server CPU/RSS excludes PostgreSQL's process/container; these measurements are
**application costs**, not total deployment costs. The workload excludes TLS,
reverse proxy, OCR, semantic search, SSE, many users/tenants and maintenance
running after its one-hour scheduler interval. All real indexing/outbox work
remains active.

## Results

All **72 measured steps** completed and passed the configured p95 <1000 ms,
zero-error budget. There were **188,950 completed measured requests**, zero
counted HTTP/transport errors, zero timeouts and zero drain cancellations.
Both providers and all three repeats completed cleanup; Docker inspection after
the run showed no remaining benchmark PostgreSQL containers.

At **two callers**, values below are medians of three per-repeat measurements.
Parentheses show the complete min–max repeat range, including the degraded
PostgreSQL repeat. Median p95 is **not** a pooled percentile. CPU/request includes
background application work during the step; RSS is the maximum sampled peak
across those three steps. PostgreSQL CPU/RSS excludes its database container.

| Provider | Scenario | req/s median (range) | p95 ms median (range) | App CPU ms/request median | App peak RSS MiB |
|---|---|---:|---:|---:|---:|
| sqlite | read | 1154.2 (1094.5–1196.2) | 2.6 (2.6–2.8) | 1.61 | 504.4 |
| sqlite | query | 744.7 (664.8–825.1) | 3.9 (3.4–4.5) | 2.51 | 507.9 |
| sqlite | shared | 625.3 (577.2–679.0) | 4.5 (4.0–5.1) | 3.03 | 511.5 |
| sqlite | mytasks | 296.3 (272.1–308.0) | 8.3 (8.0–10.2) | 6.57 | 559.8 |
| sqlite | search | 52.3 (51.8–52.9) | 42.1 (41.3–43.2) | 38.26 | 549.4 |
| sqlite | create | 143.7 (142.1–156.2) | 26.6 (23.4–30.9) | 13.19 | 607.5 |
| postgresql | read | 361.3 (240.2–374.6) | 7.0 (6.7–16.1) | 2.83 | 478.5 |
| postgresql | query | 332.3 (5.0–337.9) | 7.3 (7.3–524.9) | 3.09 | 481.2 |
| postgresql | shared | 240.1 (43.6–252.8) | 10.3 (9.1–61.8) | 4.14 | 477.9 |
| postgresql | mytasks | 134.8 (14.8–135.9) | 17.2 (17.0–182.4) | 7.83 | 553.1 |
| postgresql | search | 77.8 (16.6–102.7) | 48.1 (21.5–140.1) | 7.84 | 540.7 |
| postgresql | create | 110.0 (11.4–110.8) | 21.3 (20.9–345.8) | 17.57 | 518.3 |

SQLite engine version was **3.50.4**; PostgreSQL was **17.11**. Each PostgreSQL
repeat verified `fsync=on`, `synchronous_commit=on`, and `full_page_writes=on`.

## Interpretation and baseline quality

- SQLite search was repeatable at 51.8–52.9 requests/s and p95 41.3–43.2 ms at two
  callers. Its measured application CPU cost was 37.6–38.5 ms/request, far above
  point reads (1.5–1.7 ms/request). This supports profiling SQLite full-text/facet
  work as a next step; the capture does not identify a specific call-stack hotspot.
- PostgreSQL's first two query steps at two callers reached 332–338 requests/s
  with p95 7.3 ms. The third reached only 5.0 requests/s with p95 524.9 ms. Shared
  pages, tasks, search and creates also slowed in that repeat. The cause is
  **unresolved**: these data cannot distinguish application/background behavior
  from host/database/VM interference. The retained logs showed startup migration
  history probes and the existing My tasks ordering warning, without a measured
  request failure. Do not discard that repeat or use the median as proof of a
  stable production capacity.
- PostgreSQL requires an isolated-worker rerun (and a separate diagnostic trace
  if degradation repeats) before these figures become a tight regression gate.
  All steps passed the deliberately generous 1000 ms budget; that does not mean
  they all meet an interactive 100 ms target.
- Generator CPU remained below one average core in every measured step. This
  rules out simple saturation of its four-runtime-processor configuration, but
  does not establish that the shared machine or database was unconstrained.
- Normal creates grow the dataset, with indexing still active. Their row counts
  and warm-ups are recorded. Comparing write concurrency levels is a comparison
  of this evolving workload, not an isolated identical-size write benchmark.
- The old search p95 of 1181 ms cannot be used to calculate an optimization
  speedup: this run has a fixed fully indexed read fixture, different request
  ordering, a separate server with server GC, and different runtime sizing.
  No application optimization was made.

## Artifacts and validation

- [Raw schema v2 report and 250 ms CPU/RSS samples](performance-artifacts/2026-09-30/separate-process-results.json).
- [Chronological runner output](performance-artifacts/2026-09-30/separate-process-runner.txt).
- Per-repeat server logs are stored alongside the report as `sqlite.repeat-N.server.txt`
  and `postgresql.repeat-N.server.txt`.
- Release build and formatting/analyzer verification passed. All **156 unit tests**
  passed, including **13 harness tests**. Published-process smoke tests passed on
  both providers, and the retained in-process SQLite smoke passed. CI now runs each provider's published smoke with
  a generous 30-second latency budget, failing on missing coverage/request errors
  and retaining the report. That CI smoke validates the runner and fixture rather
  than enforcing machine-specific throughput.
