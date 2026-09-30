# Memory footprint investigation — 2026-09-30

The full host's 300+ MiB resident memory is mostly runtime/code/framework overhead plus GC capacity, not 300 MiB of live application data. The implementation now avoids runtime compilation of Wolverine adapters, unnecessary assembly discovery, retaining every migration context simultaneously, and full managed copies of PDFs. Workstation GC is the default for self-hosting. The [detailed composition investigation](runtime-mapping-composition-2026-09-30.md) distinguishes loader data from executable code and inventories individual DLLs, native libraries and JIT methods.

## What occupies the memory

A Linux `smaps` snapshot of the optimized ordinary JIT host, during the workload, measured approximately 343 MiB RSS:

| Mapping category | RSS MiB | PSS MiB |
|---|---:|---:|
| Anonymous allocations: GC heap and other native allocations | 110.2 | 110.2 |
| Runtime allocator doublemapper mappings: loader data, code and stubs | 100.7 | 100.7 |
| Managed assembly file mappings | 86.1 | 60.7 |
| Native libraries | 29.7 | 11.9 |
| Other: native heap, stacks, database mappings, etc. | 16.7 | 15.9 |

RSS counts resident pages mapped by the process; PSS apportions shared physical pages. Neither is the size of the application's live object graph. Mapping categories describe backing files, not exact allocation ownership. In particular, the anonymous category is not exclusively the managed heap. Doublemapper mappings split into about 30 MiB executable and 71 MiB writable; the subsequent dump identified 66.4 MiB of committed high/low-frequency loader heaps and about 25 MiB of code heaps in a comparable diagnostic instance. These are different observations and committed heap sizes are not RSS.

Runtime counters during this diagnostic run reported at most 75.3 MiB GC-committed memory. A prior reference-host heap dump, collected with a forced full GC, contained 37.9 MiB of live objects, including retained Roslyn caches. That earlier dump and this snapshot are different observations and must not be subtracted from one another. There is no demonstrated leak from these short runs; a long soak is still needed to assess retention over time.

The host eagerly registers 19 modules. Authentication, ASP.NET routing, Wolverine, EF contexts/models and query translation bring a substantial amount of executable code and metadata into use. Seeding and exercising more endpoints increases that footprint even with a small database. The snapshots support that explanation; they do not establish a precise per-module cost.

## Controlled comparisons

Two fresh processes/databases per provider; .NET 10.0.12, Release, ordinary framework-dependent JIT publish. A 200-item seed produces 500 indexed fixture documents. Each of six scenarios has a one-second warm-up and three-second measurement at concurrency one and two, followed by ten seconds of observation. Scheduler, indexing and durable messaging remain enabled. No OCR or semantic-search workload is included.

All comparisons use `DOTNET_PROCESSOR_COUNT=4`, a shared runner/server scope limited to 1 GiB RAM, no swap and two CPUs. PostgreSQL, when available, runs separately with 512 MiB RAM, no extra swap and one CPU. Database memory is excluded from host RSS. Writes run last and grow the fixture; different throughputs therefore create different final fixture sizes. Two short repeats establish direction, not statistical certainty or production capacity.

The WSL VM was restarted and its memory allowance increased between the earlier comparisons and the final runs. The benchmark scope's limits stayed fixed, but surrounding machine/cache conditions differed. This is another reason to treat timings and small memory differences as provisional.

| Configuration / provider | Before seed MiB | Indexed fixture MiB | Maximum sampled RSS MiB |
|---|---:|---:|---:|
| Reference runtime settings / SQLite | 251–252 | 555–576 | 664–665 |
| Static adapters + Workstation GC / SQLite | 225–232 | 309–314 | 392–397 |
| Static adapters + Workstation GC / PostgreSQL | 241–244 | 308–316 | 338–342 |
| Reference runtime settings / PostgreSQL | 263–264 | 467–574 | 526–620 |
| Final, also explicit module discovery / SQLite | 221–225 | 297 | 345–380 |
| Final + optional disabled tiered compilation / SQLite | 220 | 275–276 | 323–331 |
| Final ReadyToRun publish / SQLite | 238–239 | 316–319 | 366–392 |

The reference uses the updated source with the previous runtime choices: dynamic Wolverine compilation and Server GC. It is not a binary rebuilt from the old commit. The migration-scope correction is shared by the reference and optimized hosts. The controlled differences establish the combined runtime improvement, not isolated attribution to each source change.

Final SQLite peaks fell approximately 43–48% against the reference. However, another instrumented run with a 60-second observation reached 391 MiB. The initial exploratory 384 MiB gate is therefore too tight to promise for all runs. Earlier optimized and alternative-GC reports contain intentional budget failures; these are retained rather than relabeled as passing results.

The ReadyToRun configuration used by the Dockerfile was also published and tested in two fresh SQLite runs. All 24 measured steps passed their request checks, but the overall run exited 2 because one memory peak exceeded 384 MiB. Its snapshot had about 128 MiB of assembly mappings and 81 MiB of doublemapper mappings: precompilation trades generated code for larger assembly mappings and did not consistently lower total RSS here. The publish was constrained to 2 GiB RAM, no swap, two CPUs, one MSBuild node and two crossgen compiler workers. Those compiler-parallelism limits are now explicit in the Dockerfile. The full Docker image itself was not rebuilt or benchmarked; the measured host is its framework-dependent linux-x64 ReadyToRun publish configuration.

Final PostgreSQL attempts after the WSL restart failed during Docker provisioning with `failed to create TTRPC connection: unsupported protocol`. No app process started for those attempts. The completed PostgreSQL comparison above predates the explicit-discovery and PDF-stream changes. The user's existing app container was neither restarted nor modified.

## Implemented changes

- Generate and compile Wolverine message adapters during development/build; use static loading in Production. Release excludes the runtime-compiler package. Development retains dynamic compilation. Some compiler assemblies still ship transitively through EF design tooling; they were absent from the final memory mappings inspected. Presence on disk does not imply resident memory.
- Explicitly register built-in Wolverine integrations and stop automatic reference-graph discovery in static mode. PaperDotNet extension registration remains separate. Custom Wolverine integrations can enable `Messaging:AutomaticDiscovery=true`.
- Default to Workstation GC. A throughput-oriented build can override `-p:ServerGarbageCollection=true`.
- Dispose each module's migration scope before migrating the next module.
- Stream seekable PDFs directly to PDFium. Nonseekable blobs spool to a delete-on-close temporary file inside the rendering lock. Waiting preview requests no longer retain two full PDF arrays. OCR reads from a file stream. Render resolution and output contracts are unchanged. These improvements were functionally tested but are not responsible for the keyword-only benchmark reductions.
- Record startup, indexed-fixture, workload and post-workload memory phases. Optional RSS gates include warm-up and idle peaks and fail missing separate-server measurements. Reports record their exit code, runtime settings and effective cgroup limits.
- Bound PostgreSQL test containers and provide `run-limited.sh` for resource-constrained development machines. Build scripts disable persistent build servers.

Static adapter generation is recommended by [Wolverine's code-generation documentation](https://wolverinefx.net/guide/codegen); explicit discovery follows its [extension registration options](https://wolverinefx.net/guide/extensions). GC behavior and its throughput tradeoff are documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc).

## Settings tested and rejected as defaults

GC conservation level 9 and a Server-GC DATAS configuration with reduced growth did not consistently reduce the measured peaks enough to justify extra defaults. Neither is enabled by this change.

Disabling tiered compilation reduced runtime allocator mapping RSS in a diagnostic snapshot from about 101 to 65 MiB, and reduced whole-process peaks in two short runs. Snapshot timings differ, so this is supporting evidence rather than exact attribution. At concurrency two, median create throughput fell from 346 to 267 requests/second, query throughput from 691 to 564, and shared reads from 589 to 492. Plain reads improved; search throughput was essentially unchanged. This option remains opt-in because of the throughput tradeoff and unmeasured behavior with the production ReadyToRun image:

```yaml
environment:
  DOTNET_TieredCompilation: "0"
```

It uses a single optimizing JIT path and gives up tiered dynamic PGO. See [Microsoft's compilation settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation). The no-tiering report was collected before the harness gained explicit recording of this environment variable; the invocation above records the intended difference.

## Self-hosting implications and validation

Use the Release image with the new defaults, keyword search, and a database appropriate to the deployment. SQLite avoids a separate database service for a small instance. Do not interpret an RSS budget as a hard memory allocation ceiling, and do not set a 300 MiB container limit from these measurements. PDF rendering and OCR can add large native and child-process peaks; semantic embedding caches and large document collections were not measured here.

Getting the full feature set reliably below 200 MiB is not demonstrated by these settings. A substantially smaller target would require a reduced host with fewer modules and their dependencies, or changes to framework/model/code generation; merely disabling background schedules would not remove the loaded code. That is a distinct architectural tradeoff, not a proven low-risk tuning switch.

Validation: 157 unit tests and 22 integration tests passed after the static-discovery and PDF-stream changes, including real OCR/previews, workflows and durable jobs. Release builds had no warnings/errors; whitespace verification passed. Final SQLite and the completed earlier PostgreSQL workloads had no request errors, timeouts or cancellations. The 60-second diagnostic run and no-tiering comparisons were exploratory, not gate-certified deployment budgets. The ReadyToRun run's 384 MiB budget failure is recorded explicitly above.

Raw evidence is under [memory artifacts](performance-artifacts/2026-09-30/memory/). Earlier baseline methodology is in the [separate-process report](performance-baseline-2026-09-30.md). The runtime-mapping summaries and counters are separate diagnostic observations; no forced GC was used during latency measurements.
