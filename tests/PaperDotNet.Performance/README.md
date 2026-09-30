# API performance harness

This executable benchmarks the real API on SQLite or PostgreSQL. The default
mode launches a **published Production Kestrel host in a separate process** and
sends loopback HTTP traffic. `--in-process` retains the faster integration mode.
It is not a `dotnet test` project and does not claim arrival-rate capacity.

## Run

From the repository root:

```bash
bash tests/PaperDotNet.Performance/run.sh sqlite --smoke --gate
bash tests/PaperDotNet.Performance/run.sh postgresql --smoke --gate
bash tests/PaperDotNet.Performance/run.sh both --gate
```

On Linux/WSL with user systemd, use `bash tests/PaperDotNet.Performance/run-limited.sh both --gate`
to cap the build, runner and server together at 1 GiB RAM, no swap, and two CPUs
of quota. It refuses to start with less than 2 GiB available RAM. The disposable
PostgreSQL container has a separate 512 MiB/one-CPU limit by default. Keep the
same limits for comparisons; these limits affect throughput. A scope OOM kills
the benchmark rather than letting its memory grow across WSL.

The script builds Release with one MSBuild worker, publishes the host to
`artifacts/performance-host`, then runs the harness. Set `PERF_HOST_PATH` to reuse
an existing published DLL and skip publishing. Set `PERF_PUBLISH_RID=linux-x64`
to publish framework-dependent ReadyToRun, matching the Linux image; this may
restore runtime/Crossgen packages. A publish without a RID uses the normal JIT.
The runner records the host hash and runtime configuration.

For an existing publish:

```bash
PERF_HOST_PATH=/absolute/publish/paperdotnet.dll \
  dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll both --gate
dotnet run -c Release --project tests/PaperDotNet.Performance -- sqlite --in-process --smoke
```

PostgreSQL uses a disposable Testcontainers PostgreSQL 17 server, or
`PAPERDOTNET_TEST_POSTGRES` supplied privately in the environment. An external
server must permit creating databases/roles; the harness creates a unique
database and non-superuser role per repeat and removes only those resources,
including after failed initialization. It does not change server settings.
All PostgreSQL baselines require `fsync`, `synchronous_commit` and
`full_page_writes` to be `on`; actual version/settings are in the report.

For rootless Docker, configure Testcontainers for the daemon's socket and, if
needed, run its reaper without privileged mode. For example on this workstation:

```bash
export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/run/user/1000/docker.sock
export TESTCONTAINERS_RYUK_CONTAINER_PRIVILEGED=false
```

These are machine-specific values, not harness defaults. See the official
[Testcontainers configuration](https://dotnet.testcontainers.org/custom_configuration/).
Keep its resource reaper enabled. Normal disposal also tears down the owned
host, database, role, container and temporary files; forcibly killing the runner
can bypass external-server database cleanup.

On the reviewed workstation, the reaper still exited during startup with those
settings. The recorded baseline used `TESTCONTAINERS_RYUK_DISABLED=true` as an
explicit local workaround and verified normal container disposal. This is not
set by the harness or CI. With that workaround, forcibly killing the runner can
also leave its container behind.

## Measurement contract

- Each provider/repeat has a fresh database and server. Providers run serially.
- Seeding finishes before readiness checks. The fixture's complete expected
  non-folder search document count must match on three successive polls, and the
  alpha search count must match. Readiness failure is fatal; search is never skipped.
  This checks fixture convergence, not a universal guarantee that every background
  queue is empty.
- Read, query, member shared page, My tasks and search run against an unchanged
  fixture. Create runs last and deliberately includes indexing competition.
  Write steps record actual list row counts before/after measurement; warm-up
  also creates rows. Do not interpret write steps as identical-size fixtures.
- Every concurrency step has a separate warm-up, excluded from measured statistics.
  The ladder includes the exact requested cap (e.g. 1/2/4/8/12).
- The launch deadline stops admission. In-flight requests drain for a configured
  grace period. Successful drained requests and failed/cancelled/timed-out attempts
  are included in counts and their respective latency distributions.
- Throughput is completed successes divided by actual elapsed time including
  draining. Reports also retain configured launch duration. This is a closed-loop
  workload: a slower server reduces offered load.
- Failures preserve HTTP status, transport/exception class, timeouts and drain
  cancellations. Successful and all-attempt percentiles are separate. Empty
  distributions use `null`, never a misleading zero. Inspect sample count before
  trusting p99; smoke is fixture validation, not a performance claim.
- A report is atomically checkpointed after every measured step and completed run.
  Failed/incomplete provider coverage is recorded. Server stdout/stderr remains in
  adjacent per-repeat log files, including startup failures.

Exit codes: **0** means requested coverage completed (and all steps passed when
`--gate` is set); **1** means invalid configuration, setup/scenario/cleanup failure,
cancellation or missing coverage; **2** means a completed `--gate` run missed its
latency/error budget. In exploratory mode budget breaches remain in the JSON and
console output. A passing maximum is a lower bound, not a discovered capacity.

## Configuration

| Environment variable | Default | Meaning |
|---|---:|---|
| `PERF_ITEMS` | 200 | Base list size; shared folders/documents and 20 task lists scale with it |
| `PERF_SECONDS` | 30 | Measured launch duration per concurrency step |
| `PERF_WARMUP_SECONDS` | 5 | Warm-up per step; zero is allowed |
| `PERF_REPEATS` | 3 | Fresh fixtures/server per provider repeat |
| `PERF_MAX_CONCURRENCY` | 16 | Maximum simultaneous closed-loop callers |
| `PERF_P95_MS` | 1000 | Successful latency budget; any error/timeout/cancellation also fails a step |
| `PERF_MAX_RSS_MIB` | unset | Optional sampled server RSS budget; `--gate` fails on a breach or missing server measurements |
| `PERF_IDLE_SECONDS` | 0 | Observe server CPU/RSS after workloads without forcing GC; background jobs remain active |
| `PERF_DRAIN_SECONDS` | 10 | Grace period after launch deadline |
| `PERF_REQUEST_TIMEOUT_SECONDS` | 30 | Per-request timeout, including setup requests |
| `PERF_READY_TIMEOUT_SECONDS` | 120 | Host-listening and index-convergence deadlines |
| `PERF_OUTPUT` | `perf-results.json` | Schema v2 report; parent directory created automatically |
| `PERF_HOST_PATH` | required in process mode | Published `paperdotnet.dll` |
| `PERF_POSTGRES_IMAGE` | `postgres:17-alpine` | Pin a tag/digest for repeatable database runs |
| `PERF_POSTGRES_MEMORY_MIB` | 512 | Disposable PostgreSQL hard memory limit (128–16384 MiB); swap disabled, CPU quota one core |
| `PERF_RUNTIME_COMPILATION` | false | Diagnostic comparison only: requires a host published with `-p:EnableRuntimeCompilation=true` |

`--smoke` fixes items=20, measured seconds=1, warm-up=0.2, repeats=1 and concurrency=1.
Other timeout/budget/output settings still apply. Invalid numeric values and
unknown CLI arguments fail instead of silently selecting defaults.

## Resources and comparison

Every warm-up/measured step samples server and generator OS CPU time and RSS at
250 ms, including start/end snapshots. CPU time is reported as seconds and average
cores used; sampled peak RSS includes native and managed resident memory, not
managed allocation rate or retained heap size. Database containers and child
processes are excluded. In-process reports have no server-only resource field:
the generator process also contains the server.

Reports include commit/worktree state, runner/host hashes, runtime, architecture,
OS, exposed processor count, available cgroup limits when readable, initial
machine memory information, relevant runtime overrides, fixture size, durations,
database settings, and background configuration. Production process settings use
server GC from the publish, keyword search, a one-hour maintenance scheduler
interval, real durable messaging/indexing, raised rate limits and no OTLP export.
Normal process teardown terminates the owned host after all workload requests
have drained. Shutdown is outside measured steps.

Keep the same published binary, fixture, hardware limits, offered workload and
database durability when comparing commits. Inspect generator CPU for load
generation saturation, compare repeat distributions, and collect longer runs on
an isolated worker before setting tight gates. `DOTNET_PROCESSOR_COUNT` changes
runtime sizing; it **does not enforce a CPU quota**. Set real CPU/memory limits in
your deployment environment and retain them with the report. TLS/proxy overhead,
many tenants, OCR, SSE, semantic search and long-term memory growth require
additional workloads.

To inventory a saved Linux memory mapping capture, use:

```bash
python3 tests/PaperDotNet.Performance/analyze-smaps.py capture-smaps.txt.gz artifacts/mapping-inventory
```

This writes JSON and CSV with resident/proportional memory by backing file,
assembly group and runtime allocator permissions. It accepts plain `smaps` files
too. These mappings do not identify all native allocation owners or individual
JIT methods; see the [composition investigation](../../docs/runtime-mapping-composition-2026-09-30.md)
for the dump/trace attribution and its limits.
