# ADR-0018: Automation with rules and WorkflowCore

- **Status:** Accepted; the engine part is superseded by [ADR-0019](0019-workflows-on-wolverine.md) (no WorkflowCore)
- **Date:** 2026-09-25

## Context

Phase 5b brings rules (EVT-07), multi-step workflows with approvals and
escalations (EVT-08), triggers and actions from extensions (EVT-09) and path
templates (DOC-14). ADR-0017 planned Elsa 3 as the workflow engine.

- **Elsa 3.8+** depends on `JsonSchema.Net` 9. Its binaries come with the
  json-everything "Open Source Maintenance Fee" EULA: a monthly fee for
  organizations with more than US$10k revenue. That is commercial terms, which
  our dependency policy does not allow.
- **Elsa 3.7.1** is still license-clean, but using it has three drawbacks:
  - It would be frozen, because every upgrade brings the fee back.
  - It brings about 60 packages: FastEndpoints, NSwag, pre-1.0 CShells, and
    its own identity and tenant layers.
  - It is not covered by our tenant filters or row-level security.
- **WorkflowCore** 3.21 (MIT, actively released) is a small embeddable engine.
  - It provides durable workflows, event waits, timers and EF Core
    persistence for SQLite and PostgreSQL.
  - A spike on .NET 10 and EF Core 10 passed on both databases.

## Decision

- **Engine: WorkflowCore**, wrapped by the building block `PaperDotNet.Workflows`.
  - Only that building block and the Automation module reference WorkflowCore;
    an architecture test enforces this.
  - The engine runs in-process and starts after the migrations
    (`Workflows:Enabled`, `Workflows:PollInterval`).
  - It runs on one node, like the job scheduler.
- **Storage in the application database.**
  - PostgreSQL: WorkflowCore's own schema `wfc`, with its own migrations.
  - SQLite: WorkflowCore's SQLite provider only creates tables in a new
    database (`EnsureCreated`, no migrations). So `Persistence.Sqlite` creates
    its tables from WorkflowCore's EF model when they are missing.
  - One database file keeps backup and restore unchanged. These tables are the
    only ones we do not migrate ourselves (a documented exception).
  - WorkflowCore rows carry no tenant. Tenant data lives in our tables, and
    WorkflowCore only holds a run id and the tenant needed to resume it.
- **One engine workflow, our own interpreter.**
  - Tenants define workflows as JSON (steps: `action`, `approval`, `delay`,
    `condition` with then/else). Definitions are stored in versioned,
    tenant-owned rows.
  - A single compiled WorkflowCore workflow runs our interpreter. The
    interpreter executes the compiled steps until the run has to wait. The
    engine then waits durably (an approval event, or a timer) and calls it
    again.
  - This means no dynamic registration and no expression language from users
    (WorkflowCore's DSL evaluates Dynamic LINQ). Running runs keep their
    definition version.
- **Approvals** are rows with assignees, a due date and escalation users.
  - A decision publishes the WorkflowCore event.
  - A job every minute escalates overdue approvals. It also re-publishes
    decisions whose run still waits, in case the event was lost.
- **Rules** are our own small engine on item events and extension triggers:
  - a trigger (list, content type, changed fields), an optional OData
    condition, then ordered actions;
  - each rule runs once per event (a unique run row is written first);
  - actions act on behalf of the organization.
- **Loop protection.** Integration events carry a causation `Depth`
  (`EventCausation`, scoped).
  - A change made while handling an event of depth n gets depth n + 1.
  - Rules ignore events of depth 3 or more, so a rule that updates its own
    item runs at most three times.
- **Actions and triggers are an SDK contract** (`PaperDotNet.Automation.Contracts`:
  `IAutomationAction`, `IAutomationTriggers`, `AutomationTriggerDefinition`).
  - Built-in actions: `item.update`, `item.file` (path templates),
    `task.create`, `notify`, `workflow.start`.
  - Extensions add actions and triggers with `AddAutomationAction` and
    `AddAutomationTrigger`; both are gated per tenant.
  - Inputs may contain tokens (`{title}`, `{field:format}`, `{created:yyyy}`,
    `{outcome:Step}`, …).
  - Recipients and assignees are user names, `group:Name`, `field:name`,
    `creator` or `actor`.
- **Portable.** Definitions refer to lists, users, groups and workflows by
  name, so rules and workflows travel with workspace templates
  (`urn:paperdotnet:automation:1`).

## Consequences

- WorkflowCore's persistence packages are built against EF Core 9. They run
  on EF Core 10; re-check this on upgrades.
- Several application nodes would need one of WorkflowCore's distributed lock
  and queue providers. This is not needed for the self-hosted setup.
- Renaming a list, user or workflow breaks rules that refer to it by name.
  The rule's run then fails with a clear error in its run log.
- There is no visual designer. Workflows are JSON through the API; a future UI
  can edit that model.
