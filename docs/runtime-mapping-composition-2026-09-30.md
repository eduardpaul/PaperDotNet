# Runtime mapping composition — 2026-09-30

The earlier shorthand “101 MiB of generated code” was too broad. The `/memfd:doublemapper` backing file belongs to the runtime allocator, which also serves non-executable loader heaps. The original snapshot contains **29.93 MiB executable RSS and 70.78 MiB writable RSS**, not 101 MiB of method bodies.

## Runtime allocator: code versus loader data

The original `final-smaps.txt.gz` permissions give this exact mapping-level split:

| Permissions | Resident MiB | Meaning |
|---|---:|---|
| `r-xs` | 29.93 | Executable mappings: method code, stubs/precode, allocator capacity |
| `rw-s` | 70.78 | Writable runtime mappings: loader data and writable code aliases |
| `---s` | 0 | Reserved, inaccessible address space; does not contribute RSS |

The runtime's [loader-heap implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/utilcode/loaderheap.cpp) calls the [executable allocator](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/utilcode/executableallocator.cpp) for non-executable allocations too. The backing-file name alone therefore cannot identify code.

The original allocator's PSS was almost equal to its RSS. Despite the name “doublemapper,” this snapshot is not primarily 50 MiB counted twice. Lowering the managed GC heap limit would not directly remove these loader/code allocations.

A separate disposable instance exercised the same fixture/endpoints. After the last request measurement, it captured `smaps`, a heap-inclusive dump, and a three-second runtime loader/JIT trace with rundown. The instance, runner and dump collector were capped at 2 GiB RAM, no swap and two CPUs. The trace collector had a separate 512 MiB/one-CPU cap. No user app/container was inspected with a dump or restarted. Diagnostics perturb the process; this run is attribution evidence, not a latency comparison or gate result.

This capture had 99.88 MiB runtime-allocator RSS (29.71 executable, 70.17 writable). SOS `eeheap -loader` identified:

| Runtime heap | Committed MiB | Interpretation |
|---|---:|---|
| High-frequency loader heap | 40.66 | Runtime loader/type/method-related data |
| Low-frequency loader heap | 25.76 | Additional loader structures and bookkeeping |
| JIT loader code heaps | 23.18 | Code-heap capacity, including internal overhead |
| Dynamic/host code heaps | 1.85 | Dynamic-method code-heap capacity |
| Fixup precode heap | 6.34 | Method entry/dispatch precode infrastructure |
| Other stub/dispatch heaps enumerated | 0.86 | Stub precode, indirection cells, cache entries, etc. |

These are **committed heap ranges, not RSS**, and must not be added to mapping totals as extra memory. Loader structures include runtime representations of types/methods, generic information, dispatch and related data. The dump exposes the high/low-frequency heap categories, not an exact allocation-type or per-package ownership breakdown. Most allocations are in the shared noncollectible loader allocator; assigning the entire 66.4 MiB to EF would be unsupported. SOS terminology follows [Microsoft's diagnostic documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/sos-debugging-extension).

The trace independently enumerated **21.92 MiB of JIT method bodies across 60,416 native code addresses**, plus 2.10 MiB of non-JIT/precompiled bodies. Native addresses are deduplicated: these are code entries/versions, not distinct source methods. Shared generic instantiations and multiple optimization tiers complicate source-method counts. Body bytes exclude padding, allocation slack, loader data, GC/unwind information and stubs; they are not resident-page measurements.

| Module owning recorded JIT bodies | Body MiB | Code entries |
|---|---:|---:|
| EF Core | 3.90 | 10,484 |
| EF Core Relational | 3.02 | 6,053 |
| System.Private.CoreLib | 2.43 | 11,348 |
| SQLite migrations | 1.41 | 1,919 |
| Anonymous dynamic-method assembly | 1.10 | 3,865 |
| PaperDotNet.Lists | 1.04 | 1,690 |
| System.Text.Json | 0.85 | 2,773 |
| System.Linq | 0.82 | 2,007 |
| System.Linq.Expressions | 0.62 | 1,054 |
| Wolverine | 0.57 | 1,799 |

EF Core and its relational layer account for approximately **32% of the recorded JIT body bytes**. Attribution here means the method's defining module; generic framework methods can be instantiated on behalf of application/EF code. Anonymous dynamic methods cannot all be attributed to EF. The largest entries include EF query-shaper processing, a generated lambda, EF query translation and SQLite migration rewriting.

The tier breakdown was 12.79 MiB quick-JIT code, 7.79 MiB optimized tier-one code and 1.34 MiB other optimized JIT code. The trace includes multiple versions, so disabling tiering can affect both code and associated runtime data. This supports the earlier measured tradeoff without proving that all writable loader memory is caused by tiering.

## Assembly mappings: 86.09 MiB

The original capture maps 260 distinct DLL files:

| File group | DLL files | RSS MiB | PSS MiB |
|---|---:|---:|---:|
| .NET runtime | 63 | 38.13 | 18.74 |
| ASP.NET runtime | 80 | 19.03 | 13.07 |
| Third-party packages | 73 | 23.93 | 23.93 |
| PaperDotNet | 44 | 5.00 | 5.00 |

Largest individual mappings:

| DLL | RSS MiB |
|---|---:|
| System.Private.CoreLib | 12.30 |
| System.Private.Xml | 3.13 |
| Microsoft.EntityFrameworkCore | 2.70 |
| System.Linq.Expressions | 2.50 |
| Wolverine | 2.43 |
| Kestrel Core | 2.29 |
| System.Security.Cryptography | 2.23 |
| EF Core Relational | 2.09 |
| System.Text.Json | 1.69 |
| System.Data.Common | 1.68 |
| OpenAI | 1.62 |
| Microsoft.OData.Core | 1.47 |

Assembly mappings contain IL, metadata, resources, and ReadyToRun code where present; “ordinary JIT host” still uses ReadyToRun framework assemblies. They are distinct from the writable loader structures above. Most DLL mapping RSS is clean file-backed memory. PSS apportions shared physical pages: the 86.09 MiB RSS corresponds to 60.74 MiB PSS in this snapshot. Sharing depends on other processes and file identities, so that reduction is not a fixed deployment guarantee.

PaperDotNet's own mapped DLLs are a small part of this category. Removing one small module does not automatically remove its framework dependencies, JIT code, or all loader data. Conversely, OpenAI, MCP and PostgreSQL-related assemblies are resident even in this keyword/SQLite fixture. That is measured evidence of dependencies present beyond the workload; their incremental cost must be tested before changing registration or offering reduced host profiles. Runtime compiler/Roslyn assemblies were absent from these inspected host mappings.

## Native library mappings: 29.73 MiB

| Native group | RSS MiB | What it supplies |
|---|---:|---|
| CoreCLR | 5.95 | Managed execution, GC and runtime support |
| JIT compiler | 2.91 | Compiles managed methods; separate from emitted code |
| ICU data/internationalization/core | 8.14 | Unicode/culture-aware globalization |
| OpenSSL crypto + SSL | 4.77 | Cryptography and TLS support |
| libc + libstdc++ | 3.32 | Native standard libraries |
| SQLite engine | 1.13 | Embedded database |
| All other native libraries | 3.53 | Runtime hosting, math, tracing, compression, unwinding, etc. |

ICU's data file reserves a much larger mapping than its resident pages; only resident pages count in this table. Native library PSS totaled 11.87 MiB, much lower than RSS because many clean pages were shared. The table covers mapped library pages, **not native allocations made by those libraries**; their malloc allocations can appear in `[heap]` or anonymous mappings.

PDFium, Skia and Tesseract were not part of these mapped-library totals. The workload did not render/OCR documents. Tesseract, when invoked, runs as a child process and is excluded from the harness's server-process RSS.

## What this changes about the optimization priorities

The dominant actionable target is loader/type/method proliferation and EF/model/query/migration initialization, not the 30 MiB of native libraries or PaperDotNet's 5 MiB of file mappings. Separating startup-only migration work into another process and testing a reduced host with selected modules are sensible next experiments; neither has an established savings figure yet. Compiled EF models/queries are also candidates, but can trade reflection/dynamic work for more generated code, so they require measurement.

Disabling globalization or removing native libraries would change supported behavior for relatively limited savings. No such settings or feature removals were applied during this investigation.

The exact files and runtime heaps are now inventoried. Exact attribution of the shared 66.4 MiB loader-data capacity to each package or type remains unresolved; that would require deeper allocation instrumentation rather than guessing from DLL sizes or JIT-body ownership.

## Reproducing the inventory

```bash
python3 tests/PaperDotNet.Performance/analyze-smaps.py \
  docs/performance-artifacts/2026-09-30/memory/final-smaps.txt.gz \
  artifacts/mapping-inventory
```

The script groups backing files and sums kernel-reported values; it does not infer native allocation ownership. Its categories were checked against the prior analysis of the same source file. `smaps` and `smaps_rollup` were read sequentially while the instance remained active: their totals differ slightly, including a 1.19 MiB RSS difference in the original capture. These are not atomic simultaneous snapshots. Full [file inventory](performance-artifacts/2026-09-30/memory/mapping-inventory.csv), [method/heap summary](performance-artifacts/2026-09-30/memory/loader-attribution.json), compressed mapping captures and SOS output are retained with the [memory evidence](performance-artifacts/2026-09-30/memory/). The process dump, binary trace, duplicate method exports and machine-specific capture scripts remain in ignored local artifacts; they are not added to the repository.
