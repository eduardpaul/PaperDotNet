# Automation

Automation reacts to changes in a workspace (EVT-07…09, DOC-14). Design:
[ADR-0024](adr/0024-one-automation-model.md) (one model) and
[ADR-0019](adr/0019-workflows-on-wolverine.md) (how runs are resumed).

An **automation** has a **trigger**, an optional **condition** and **steps**.
The steps can act right away ("file new invoices and create a review task") or
wait ("ask the manager to approve, escalate after 48 hours, then mark the
invoice").

Automations belong to a workspace. Everyone who can read the workspace sees
them; only workspace managers change them. They refer to lists, users and
groups **by name**, so they are portable in templates.

## Defining an automation

`POST /v1.0/workspaces/{ws}/automations`

```json
{
  "name": "File invoices",
  "trigger": { "type": "itemAdded", "list": "Invoices", "contentType": "Invoice" },
  "condition": "fields/amount gt 100",
  "steps": [
    { "type": "action", "action": "item.file", "inputs": { "folder": "{created:yyyy}/{counterparty}", "title": "{counterparty} {invoiceNumber}" } },
    { "type": "action", "action": "task.create", "inputs": { "list": "Tasks", "title": "Check {title}", "assignedTo": ["group:Accountants"], "dueInDays": 3 } },
    { "type": "approval", "name": "Manager", "assignees": ["field:approver"], "title": "Approve {title}",
      "dueInHours": 48, "escalateTo": ["group:Finance leads"] },
    { "type": "condition", "step": "Manager", "is": "approved",
      "then": [ { "type": "action", "action": "item.update", "inputs": { "fields": { "status": "approved" } } } ],
      "else": [ { "type": "action", "action": "notify", "inputs": { "to": ["creator"], "title": "{title} was rejected" } } ] }
  ]
}
```

**Trigger** (`GET /v1.0/automation/triggers`):

- `type`:
  - `manual`: a person starts it on an item (see below);
  - `itemAdded`, `itemUpdated`, `itemDeleted` (moved to the recycle bin),
    `itemRestored`;
  - or an extension trigger.
- `list`, `contentType` (name or key) and, for updates, `changedFields`
  narrow it down. Folders never trigger automations.

**Condition:** an OData filter on the item, as in the items API. It needs the
trigger's `list` and is checked when the trigger fires. An item that does not
match starts nothing. A condition that can no longer be checked (for example,
a field was removed) gives a failed run with the error.

**Steps** run in order, on behalf of the organization:

- `action`: runs an action with `inputs`. A failure fails the run.
- `approval`: waits until one of the `assignees` decides `approved` or
  `rejected`.
  - Overdue requests (`dueInHours`) are escalated: the `escalateTo` people are
    added as assignees and everyone is notified.
  - Needs a `name`: later conditions and tokens refer to it.
- `condition`: either `step` + `is` (an approval outcome) or `filter` (OData
  on the item), then `then` / `else` steps.
- `delay`: waits `hours` (the run continues within a minute of the time).

Approval steps and `filter` conditions need an item. Extension triggers without
an item fail there.

Changing an automation creates a new version. Running runs keep the version
they started with.

## Runs

- **When runs start:** every trigger that matches starts one run, and the same
  event never starts a second run of the same automation.
- **Manual start:** start a `manual` automation on an item with
  `POST …/lists/{list}/items/{item}/automations` `{ "automation": "Invoice approval" }`.
  - You need Contribute access to the item.
  - The item must match the trigger's list and content type, and the
    condition.
- **Status and log:** `GET …/automations/runs?automationId=&itemId=&status=`
  shows each run's status (`running`, `waiting`, `completed`, `failed`,
  `cancelled`), the approval outcomes, a log and the error. Cancel a run with
  `POST …/automations/runs/{id}/cancel`.
- **Retries and servers** ([ADR-0025](adr/0025-reliable-runs-on-several-servers.md)):
  - Only one server executes a run at a time (a lease).
  - A step that fails with an error (rather than a failed action) is retried
    from where it stopped. Actions are written to be safe to repeat.
  - Runs whose server crashed or whose message was lost are resumed by the
    minute job within a few minutes.
  - A run that makes no progress after 10 attempts fails.
- **Loops:** changes made by automation trigger automations again up to a depth
  of 3, then stop. An automation that updates its own item cannot loop forever.
- **Retention:** finished runs are deleted after 30 days
  (`Automation:RunRetentionDays`).

## Approvals

- `GET /v1.0/me/approvals` lists your pending approvals (`?status=approved`,
  `rejected`, `cancelled`).
- Decide with `POST /v1.0/me/approvals/{id}/decision`
  `{ "outcome": "approved", "comment": "…" }`.

## Actions

| Action | Inputs |
|---|---|
| `item.update` | `fields`: values to set; text may contain tokens |
| `item.file` | `folder`: path template (each level becomes a folder; missing ones are created); `title`: new title |
| `task.create` | `list` (a task list), `title`, `assignedTo`, `dueInDays`, `priority`, `description` |
| `notify` | `to`, `title`, `body`; the notification links to the item |
| `{extension}.…` | Actions of enabled extensions (`GET /v1.0/automation/actions`) |

**Tokens** in text inputs:

- `{title}`, `{fieldName}`, `{fieldName:format}` (dates and numbers);
- `{created:yyyy}`, `{modified}`, `{today:yyyy-MM-dd}`;
- `{list}`, `{id}`;
- `{outcome:Step}` (approval outcomes);
- `{data:name}` (extension trigger data).

Term values become term names and person values become user names. `{{` and
`}}` are literal braces.

**People** (`to`, `assignedTo`, `assignees`, `escalateTo`):

- user names;
- `group:Name` (its members);
- `field:name` (a person field of the item);
- `creator` (of the item);
- `actor` (the user who started the run or whose change triggered it).

## Extensions

Extensions add actions with `builder.AddAutomationAction<T>()` (implement
`IAutomationAction`). They add triggers with
`builder.AddAutomationTrigger(new(key, description))` and raise them with
`IAutomationTriggers.RaiseAsync`. Keys start with the extension id. Both only
work in tenants where the extension is enabled.

Actions must be safe to run again: the same step can run again after a crash
with the same `context.ExecutionKey` and `context.ExecutionId`. Use the id as
the id of what the action creates (for example
`IListItemStore.CreateAsync(workspaceId, listId, context.ExecutionId, …)`),
and the key to find what an earlier attempt did or as a deduplication key. See `docs/extensions.md` and the
sample `samples.invoices` (trigger `approvalNeeded`, action `approve`).
