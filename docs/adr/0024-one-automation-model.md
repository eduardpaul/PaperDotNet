# ADR-0024: One automation model (rules and workflows merged)

- **Status:** Accepted (changes the model of ADR-0018; keeps the run engine of ADR-0019)
- **Date:** 2026-09-25

## Context

Automation had two engines that shared only actions, tokens and templates:
- **Rules** had their own trigger, a condition and actions. They ran inside
  the event handler in a single pass. A run row was written first to block
  duplicates, which made rules run at most once: a crash or a failing action
  left the row "Running", and the retry skipped the rule. Any database error
  was also treated as "already handled".
- **Workflows** had steps (actions, approvals, delays, conditions), versions,
  logs and cancellation. They were resumed through outbox messages, with
  progress saved after every step. They had no trigger of their own: they
  started from an item or from a rule's `workflow.start` action.

A rule is a workflow with only action steps and a trigger. Two APIs, two run
tables and two runners were duplication, and the weaker one had the
reliability bugs.

## Decision

- **One model: an automation.** It has:
  - a **trigger:** `manual`, `itemAdded`, `itemUpdated`, `itemDeleted`,
    `itemRestored` or an extension trigger, narrowed by list, content type and
    `changedFields`;
  - an optional OData **condition** on the item;
  - **steps:** `action`, `approval`, `delay` and `condition`.

  The whole definition is versioned, and a run keeps the version it started
  with.
- **Starting.** `AutomationTriggerHandler` (an event subscriber) checks the
  trigger filters and the condition when it handles the event. It saves one
  run per matching automation, together with its `ResumeRun` message, in one
  transaction (transactional outbox).
  - A unique index on (automation, event) and a check before saving make a
    redelivered event start nothing.
  - A condition that cannot be checked (for example, a removed field) gives a
    failed run, so it is visible.
  - An item that does not match starts nothing and leaves no row.
- **Running.** The existing interpreter (ADR-0019) runs every automation, so
  what used to be a rule now gets progress saved per step, retries from where
  it stopped, logs, versions and cancellation.
  - A run's item is optional: extension triggers may have none. Approval and
    filter steps then fail with a clear error.
  - Trigger data is stored on the run for `{data:…}` tokens.
- **Manual start.** `POST …/items/{id}/automations {"automation": name}` starts
  only automations with the `manual` trigger (Contribute on the item). The
  trigger's list, content type and condition must match the item.
  `workflow.start` is removed: an automation reacts to the event directly.
- **Retention.** A daily job deletes finished runs, and their approvals, after
  `Automation:RunRetentionDays` (30).
- **API:**
  - `/workspaces/{ws}/automations`;
  - `/workspaces/{ws}/automations/runs?automationId=&itemId=&status=`, and
    `…/runs/{id}/cancel`;
  - the item start endpoint;
  - `/me/approvals`, unchanged;
  - the catalog under `/v1.0/automation`.

  Templates use the section `Automations` in `urn:paperdotnet:automation:2`.
  Existing rules and workflows are dropped by the migration (pre-release).

## Consequences

- Automations run at least once per event. Actions must be safe to repeat
  with the same `ExecutionKey` (unchanged from ADR-0018).
- Every firing is a run row and one more queued message. That is a little
  more delay and storage than an in-handler rule; retention bounds the growth.
- The condition is checked when the event is handled, against the item as it
  is then (not as it was at the change). With a short queue the difference is
  negligible; a snapshot on the event would be needed to remove it.
