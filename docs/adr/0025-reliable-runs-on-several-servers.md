# ADR-0025: Reliable automation runs and folder moves on several servers

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

The review of events and automation for multi-server deployments (after
ADR-0023 and ADR-0024) found gaps. In each case a crash, a retry or two
servers could lose or repeat work:

- **Approval hand-off:** the approval was saved and its assignees notified
  before the run was marked as waiting. A decision in that gap was ignored and
  the run stuck. A retry reused the approval without notifying anyone.
- **Double execution:** nothing stopped two servers from executing the same
  run. The version check only noticed afterwards, after an action had run
  twice.
- **Stuck runs:** a run whose message was lost or dead-lettered stayed
  "Running" forever. A decision whose resume message was lost left its run
  waiting forever.
- **Timer resends:** the minute job resent the resume message for every
  overdue delay each minute, 500 at a time and in no order.
- **Escalation vs. decision:** both update the approval. The loser got a false
  "already decided" (409), or the job stopped escalating for that tenant.
- **Non-repeatable actions:** `task.create` created a second task when a step
  ran again.
- **Folder moves:** a folder was saved, and then the items inside it were
  reassigned to its new permission scope in separate statements. A crash in
  between left them with the old permissions. Wolverine's outbox commits the
  database transaction itself, so it cannot be wrapped in an outer one.

## Decision

- **Leases.** A handler claims a run with a conditional update (`LeaseId`,
  `LeaseUntil` = now + 5 minutes, `Attempts + 1`) before executing it.
  - A run leased elsewhere makes the message fail and retry later
    (`RunLeasedException`).
  - Every save extends the lease; stopping (finished or waiting) releases it,
    and so does an exception.
  - A crashed handler's lease expires.
  - A run that is claimed more than 10 times without progress is failed.
    `Attempts` is reset whenever the run makes progress.
- **Recovery.** The minute job also resends `ResumeRun` in two cases:
  - running runs without a lease and without progress for 2 minutes (lost or
    dead-lettered messages, crashed handlers);
  - waiting runs whose approval was decided more than 2 minutes ago.

  A run it resumes is not looked at again for 5 minutes (`NextCheckAt`), and
  the oldest runs go first.
- **Approvals.** The request, the run's wait and a `NotifyApproval` message
  are saved in one transaction. Notifications are sent by that message's
  handler, deduplicated by approval. Escalation saves the escalated request
  with its `NotifyApproval` message. A concurrency conflict retries the
  decision, and makes the job skip that request until the next minute.
- **Repeat-safe actions.** An action step gets a stable
  `AutomationActionContext.ExecutionId`, saved on the run before the action
  runs. `IListItemStore.CreateAsync(workspaceId, listId, itemId, …)` creates
  an item with a given id, or returns it when it exists. `task.create` uses
  the execution id as the task id, so a repeated step creates one task.
- **Folder moves.** A folder change that changes its permission scope is
  saved with a durable `CompleteFolderScopeChange` message. The request still
  reassigns the items inside right away. The message's handler repeats the
  reassignment, which is idempotent: it does nothing when the request already
  did it or the folder has moved since. It only invalidates the search index
  when it changed something.

## Consequences

- At most one server executes a run at a time; lost messages and crashed
  servers delay a run by minutes but no longer stop it.
- Actions from extensions should use `ExecutionId` or `ExecutionKey` to be
  safe to repeat. The rare repeat after a crash between an action and the
  save of its step remains.
- Still open for multi-server deployments: live events (`/v1.0/me/events`)
  reach only clients connected to the server that raised them. That needs a
  shared channel, such as PostgreSQL LISTEN/NOTIFY.
