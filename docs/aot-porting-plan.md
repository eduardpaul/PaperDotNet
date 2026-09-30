# Porting to the Native AOT server (ADR-0039)

Goal: every module of the .NET 10 server working in the Native AOT server. Order: dependencies first, then complexity
(S, M, L, XL). Each task is committed on its own; a module is done when it builds under the AOT rules, its tests from
`tests/PaperDotNet.IntegrationTests/ToPort` pass again (adapted), and `eng/aot-smoke.sh` passes.

| # | Size | Task | Status |
|---|---|---|---|
| T01 | S | Housekeeping: dev launch settings, GLM image, compose overrides, samples | done (samples come with T09) |
| T02 | S | Storage building block (`IBlobStore`, local disk) | |
| T03 | S/M | Jobs: recurring tenant jobs (Cronos) and operations (202 + `/operations/{id}`) | |
| T04 | M | Identity parity: tenants admin, user lifecycle, groups (nested), directory, preferences, API tokens | |
| T05 | M | Workspaces with members and roles | |
| T06 | L | Lists parity 1: workspace-scoped API, content types, all field types, folders | |
| T07 | L | Lists parity 2: versions and history, recycle bin, item mutators, full item events | |
| T08 | XL | Lists parity 3: permissions (ADR-0035), indexed fields, views, templates, smart folders, delta, bulk | |
| T09 | M | Extension SDK, extension host and generator | |
| T10 | S-M | Notes, Collaboration, Notifications | |
| T11 | M | Tasks and Calendar | |
| T12 | L | Taxonomy and Search (SQLite FTS5, optional semantic search) | |
| T13 | M | Provisioning and templates | |
| T14 | L | Workflows parity (waits, approvals, schedules, `forEach`, `event.raise`, `steps`, built-ins) and AI workflows | |
| T15 | XL | Documents (PDF libraries, page images, OCR) | |
| T16 | M | MCP, admin CLI, backup and restore, Papermerge import | |
| T17 | XL | PostgreSQL build, SDK regeneration, web UI | |

Done before this plan: building blocks, Identity (sign-in slice), Lists (slice), Audit (slice), Workflows (flow slice).
