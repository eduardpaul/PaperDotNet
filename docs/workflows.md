# Workflows

Workflows react to changes in a workspace (EVT-07…09, DOC-14). Design:
[ADR-0036](adr/0036-workflows-as-the-core.md) (workflows as the core, the plan),
[ADR-0024](adr/0024-one-workflow-model.md) (one model) and
[ADR-0019](adr/0019-workflows-on-wolverine.md) (how runs are resumed).
Workflows were called *workflows* before ADR-0036.

An **workflow** has a **trigger**, an optional **condition** and **steps**.
The steps can act right away ("file new invoices and create a review task") or
wait ("ask the manager to approve, escalate after 48 hours, then mark the
invoice").

Workflows belong to a workspace. Everyone who can read the workspace sees
them; only workspace managers change them. They refer to lists, users and
groups **by name**, so they are portable in templates.

## Defining a workflow

`POST /v1.0/workspaces/{ws}/workflows`

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

**Trigger** (`GET /v1.0/workflows/triggers`):

- `type`:
  - `manual`: a person starts it on an item (see below);
  - `itemAdded`, `itemUpdated`, `itemDeleted` (moved to the recycle bin),
    `itemRestored`;
  - or an extension trigger.
- `list`, `contentType` (name or key) and, for updates, `changedFields`
  narrow it down. Folders never trigger workflows.

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

Changing a workflow creates a new version. Running runs keep the version
they started with.

## Flows

Instead of `steps`, a workflow can have a `flow` ([ADR-0036](adr/0036-workflows-as-the-core.md)):
nodes connected by the outcome of each node. Steps are compiled into the same
kind of flow, so both run the same way.

```json
{
  "name": "Check big bills",
  "trigger": { "type": "itemAdded", "list": "Bills" },
  "variables": { "limit": 100 },
  "flow": {
    "start": "big?",
    "nodes": {
      "big?":   { "activity": "if", "inputs": { "left": "{amount}", "op": "gt", "right": "{var:limit}" },
                  "next": { "true": "task", "false": "done" } },
      "task":   { "activity": "task.create", "inputs": { "list": "Tasks", "title": "Check {title}" },
                  "next": { "done": "note", "error": "report" }, "retry": { "attempts": 3, "delayMinutes": 10 } },
      "note":   { "activity": "item.update", "inputs": { "fields": { "note": "Task {step:task.taskId}" } } },
      "report": { "activity": "notify", "inputs": { "to": ["creator"], "title": "No task: {step:task.error}" } },
      "done":   { "activity": "end" }
    }
  }
}
```

- **Nodes** run an `activity` with `inputs`, then continue with the node that
  `next` names for their outcome. When the outcome's own port is not
  connected, `done` is used. A node without a next node ends the run.
- **Activities:**
  - any action (`item.update`, `task.create`, extension actions, …): ports
    `done` and `error`;
  - `approval` (inputs as the approval step): ports `approved`, `rejected`;
  - `delay` (`hours`): port `done`;
  - `if`: `filter` (OData on the item), `step` + `is` (an approval outcome)
    or `left`, `op` and `right` (text with tokens; `op` is `eq`, `ne`, `gt`,
    `ge`, `lt`, `le`, `contains`, `empty` or `notEmpty`, and numbers compare
    as numbers): ports `true`, `false`;
  - `setVariable` (`name`, `value`: text with tokens or any JSON value):
    port `done`;
  - `end` ends the run; `fail` (`message`) ends it as failed.
- **Outputs and variables:** each node's result is its output (an action's
  output, an approval's `outcome`, `decidedBy` and `comment`, or `error` on the
  `error` port). `variables` gives the initial values of the run's variables.
- **Failures:** a failing node is tried again by its `retry` policy
  (`attempts` up to 10, `delayMinutes`, default 1), then continues on its
  `error` port, and otherwise fails the run at that node.
- **Checks:** the start and every next node must exist, ports must fit their
  activity, every node must be reachable from the start, and a flow has at
  most 100 nodes. A run executes at most 1000 nodes.

The web editor edits steps; flows are edited in its JSON view for now.

## Runs

- **When runs start:** every trigger that matches starts one run, and the same
  event never starts a second run of the same workflow.
- **Manual start:** start a `manual` workflow on an item with
  `POST …/lists/{list}/items/{item}/workflows` `{ "workflow": "Invoice approval" }`.
  - You need Contribute access to the item.
  - The item must match the trigger's list and content type, and the
    condition.
- **Status and log:** `GET …/workflows/runs?workflowId=&itemId=&status=`
  shows each run's status (`running`, `waiting`, `completed`, `failed`,
  `cancelled`), its `node`, the approval `outcomes`, the `outputs` of the
  nodes that ran, its `variables`, a log and the error. Cancel a run with
  `POST …/workflows/runs/{id}/cancel`.
- **Retrying failed runs:** a run that failed at a node (`failedNode`) runs
  again from that node with `POST …/workflows/runs/{id}/retry`, for example
  after a missing list was created. The node's action keeps its execution id,
  so it does not repeat what an earlier attempt already did.
- **Waits** (approvals, delays, retries) are durable bookmarks: a waiting run
  holds no server and continues when its bookmark is completed.
- **Retries and servers** ([ADR-0025](adr/0025-reliable-runs-on-several-servers.md)):
  - Only one server executes a run at a time (a lease).
  - A step that fails with an error (rather than a failed action) is retried
    from where it stopped. Actions are written to be safe to repeat.
  - Runs whose server crashed or whose message was lost are resumed by the
    minute job within a few minutes.
  - A run that makes no progress after 10 attempts fails.
- **Loops:** changes made by workflow trigger workflows again up to a depth
  of 3, then stop. A workflow that updates its own item cannot loop forever.
- **Retention:** finished runs are deleted after 30 days
  (`Workflows:RunRetentionDays`).

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
| `{extension}.…` | Actions of enabled extensions (`GET /v1.0/workflows/activities`) |

**Tokens** in text inputs:

- `{title}`, `{fieldName}`, `{fieldName:format}` (dates and numbers);
- `{created:yyyy}`, `{modified}`, `{today:yyyy-MM-dd}`;
- `{list}`, `{id}`;
- `{outcome:Step}` (approval outcomes);
- `{var:name}` (variables), `{step:Node.path}` (a value in a node's output,
  e.g. `{step:task.taskId}`);
- `{trigger:name}` or `{data:name}` (extension trigger data).

Term values become term names and person values become user names. `{{` and
`}}` are literal braces.

**People** (`to`, `assignedTo`, `assignees`, `escalateTo`):

- user names;
- `group:Name` (its members);
- `field:name` (a person field of the item);
- `creator` (of the item);
- `actor` (the user who started the run or whose change triggered it).

## Export and import

Workflows travel in the same templates and packages as lists and libraries
([provisioning.md](provisioning.md), [export-and-import.md](export-and-import.md)):

- A workspace's workflows are the section `Workflows` in
  `urn:paperdotnet:workflow:1`, one `Workflow` element per workflow with its
  current definition as JSON and the attributes `Name`, `Description` and
  `Enabled`.
- Workflows are matched by name. A changed definition becomes a new version,
  and applying the same template again changes nothing.
- The section is checked in the dry run like the rest of the template: an
  unknown trigger or action (for example of an extension that is not enabled
  in the target) fails it with the workflow's name.
- Runs, approvals and waits are never exported.
- Templates made before the rename (section `Automations` in
  `urn:paperdotnet:automation:2`) are still read.

## Extensions

Extensions add activities with `builder.AddWorkflowActivity<T>()` (implement
`IWorkflowActivity`). They add triggers with
`builder.AddWorkflowTrigger(new(key, description))` and raise them with
`IWorkflowTriggers.RaiseAsync`. Keys start with the extension id. Both only
work in tenants where the extension is enabled.

Actions must be safe to run again: the same step can run again after a crash
with the same `context.ExecutionKey` and `context.ExecutionId`. Use the id as
the id of what the action creates (for example
`IListItemStore.CreateAsync(workspaceId, listId, context.ExecutionId, …)`),
and the key to find what an earlier attempt did or as a deduplication key. See `docs/extensions.md` and the
sample `samples.invoices` (trigger `approvalNeeded`, action `approve`).
