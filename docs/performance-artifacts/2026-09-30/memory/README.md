# Memory evidence

See the [memory investigation](../../../memory-footprint-2026-09-30.md) and [composition follow-up](../../../runtime-mapping-composition-2026-09-30.md) for interpretation and limitations.

The retained runner reports contain the primary before/after measurements:

- `limited-reference.json` and `limited-minimal.json`: two completed repeats per database provider.
- `limited-final.json`: completed SQLite runs and two PostgreSQL provisioning failures.
- `r2r-final.json`: two completed SQLite repeats; one exceeded the exploratory 384 MiB budget.
- `no-tiering.json`: two exploratory SQLite repeats with `DOTNET_TieredCompilation=0`, without gating.

The earliest reports predate persisted `exitCode`. Paths in the raw reports retain their original local provenance. `diagnostic-summary.json` preserves compact results and source hashes for the alternative-GC and instrumented runs. Full local logs and diagnostic scripts are archived under ignored `artifacts/review-evidence/memory`.

`mapping-inventory.json` and `.csv` inventory the original mapping snapshot. Compressed `final-smaps` and `loader-smaps` files preserve source mappings for the reusable `tests/PaperDotNet.Performance/analyze-smaps.py` utility. `snapshot-comparison.json` records differences from sequential `smaps_rollup` reads; these are not atomic simultaneous snapshots.

`loader-attribution.json`, `jit-by-module.csv` and `loader-sos.txt` retain the loader-heap and method-rundown findings. Method-body bytes, committed heap capacity and resident pages are distinct measurements. The loader diagnostic collected its dump and trace after all request measurements; idle samples were affected by diagnostics. The original heap excerpt is a separate, earlier full-GC observation.

Process dumps, binary traces, duplicate method exports, machine-specific capture scripts, and verbose logs are intentionally excluded from Git. Supported future workload commands are documented in `tests/PaperDotNet.Performance/README.md`.
