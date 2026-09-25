# ADR-0019: Workflow runs resumed through Wolverine messages (replaces WorkflowCore)

- **Status:** Accepted (supersedes the engine part of ADR-0018)
- **Date:** 2026-09-25

## Context

ADR-0018 put WorkflowCore under automation workflows. Our own code already did
almost everything:
- the step interpreter, definitions and versions;
- run state (position, outcomes, log);
- approvals, escalation and tenancy.

WorkflowCore only provided a durable "wait for the approval event", a timer for
delay steps, and retries. For that it added:
- about 10 packages, with persistence built against EF Core 9;
- a workaround that creates its SQLite tables;
- its own schema, outside our tenant filters and row-level security;
- a second polling loop;
- non-atomic hand-offs: a race when a run started, and a lost-decision
  re-publish job.

Our messaging layer (Wolverine, ADR-0008) already provides durable local
queues, a transactional outbox with messages (`IOutbox`), retries and a
dead-letter queue, on SQLite and PostgreSQL.

## Decision

- **No workflow engine dependency.** A run is resumed by the tenant message
  `ResumeRun(runId, waitKey)`, handled by the interpreter.
  - **Start:** the run and its first `ResumeRun` are saved in one transaction.
  - **Approval decided:** the decision and `ResumeRun` are saved in one
    transaction. The approval row holds the outcome.
  - **Delay:** the run stores `WaitingFor = delay:…` and `ResumeAt`. The
    minute job `automation.timers` sends `ResumeRun` for runs that are due;
    the same job escalates overdue approvals.
- **Stale and duplicate messages are harmless.**
  - A message names the wait it ends; the interpreter ignores it when the run
    is not in that wait, has finished, or its time has not come.
  - The run's version (optimistic concurrency) plus Wolverine retries handle
    concurrent handlers.
  - Actions must be safe to run again (unchanged from ADR-0018).
- **Delay granularity is one minute.** That is fine for business delays
  measured in hours.
- Rules, the interpreter, steps, actions, approvals, the API and templates are
  unchanged from ADR-0018.
- The WorkflowCore implementation is kept on the branch
  `claude/automation-workflowcore` for reference.

## Consequences

- One fewer dependency family and no third-party tables. All automation data
  is tenant-owned (row-level security on PostgreSQL) and in our migrations.
- Parallel branches, loops or sagas are not supported. If they are ever
  needed, revisit a workflow engine; the step model and API can stay.
