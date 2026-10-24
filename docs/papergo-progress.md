# PaperGo alignment delivery

Work is performed directly on `main`, in small commits. The compatible content
engine targets PaperGo commit `485be496d5c3b9077c9a6238cd8f11d3e4bffeda`.

| Milestone | State | Commit |
|---|---|---|
| History defaults and explicit recursive folder deletion | Pushed to main | `5bcdb0a` |
| Exact EF database, immutable revisions, publishing/archive lifecycle, CLI and interoperability probes | Pushed to main | `b189009` |
| Authenticated content API, independent grants, file revision access and source migration | Pushed to main | `4360de5` |
| Content UI, schema forms, settings, history and import | Browser scenarios passed; included with this tracker | — |
| Compatible organization databases included in installation backups/restores | Pushed to main | `4360de5` |
| Updated SDKs and final provider/browser regression checks | In progress | — |

Migration preserves identifiers, metadata, available history, file references and
content grants. Original content becomes read-only after cutover. The original
store did not retain historical schemas; imported history is paired with the
available schema. Comments, tasks, calendars, workflow runs and other module data
remain in module storage. Integration of these modules with the compatible
content lifecycle remains separate work; this tracker does not certify full
PaperGo parity. Detailed requirements and broader gaps are in
[papergo-parity.md](papergo-parity.md).

Validation so far (2026-10-24): zero-warning integration build; 26 focused SQLite
integration tests; 16 PostgreSQL engine tests; 24/25 PostgreSQL integration tests
on the first run, with all four content API tests passing after the nullable
timestamp fix; 44 web unit tests, typecheck/lint/format, and two browser scenarios.
Final full regression results will be recorded below.
