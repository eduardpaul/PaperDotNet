# ADR-0010: Recurring jobs with a small scheduler + Cronos (instead of Quartz.NET)

**Status:** Accepted (2026-09-24)

## Context
The technical approach planned Quartz.NET for cron-style schedules. With
SQLite as the default database (ADR-0009), Quartz's ADO job store needs
provider-specific SQL scripts maintained outside EF migrations. Wolverine
(ADR-0008) already covers delayed/scheduled one-off messages but not cron.

## Decision
- **One-off delayed work:** Wolverine scheduled messages (`IMessageScheduler`).
- **Recurring work:** a small in-process `RecurringJobScheduler` in the Jobs
  module:
  - jobs implement `ITenantRecurringJob` and are registered with
    `AddTenantRecurringJob<TJob>(name, cron)`;
  - cron expressions (5 fields, or 6 with seconds; UTC) are parsed with
    **Cronos** (MIT);
  - state lives in `jobs.recurring_jobs` (EF, both providers); a run is
    claimed by advancing `NextRunAt` with an optimistic-concurrency check, so
    only one node runs it;
  - a due job runs once per active tenant, each inside its tenant scope;
    failures are recorded per job and never stop the scheduler.
- **Long-running operations** (EVT-06): `IOperations.StartAsync` stores an
  operation and a `RunOperation` message atomically; handlers implement
  `OperationHandler<TPayload>`; clients poll `GET /v1.0/operations/{id}`.

## Consequences
- No Quartz dependency or extra schema scripts; everything is in EF migrations.
- Missed runs while the app was down run once at the next tick (no catch-up
  of every missed occurrence).
