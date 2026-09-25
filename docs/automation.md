# Automation: rules and workflows

Automation reacts to changes in a workspace (EVT-07…09, DOC-14). Design:
[ADR-0018](adr/0018-automation-workflowcore.md).

- A **rule** runs actions right away when something happens, e.g. "file new
  invoices and create a review task".
- A **workflow** runs steps that can wait, e.g. "ask the manager to approve,
  escalate after 48 hours, then mark the invoice".

Rules and workflows belong to a workspace. Everyone who can read the workspace
sees them; only workspace managers change them. They refer to lists, users,
groups and workflows **by name**, so they are portable in templates.

## Rules

`POST /v1.0/workspaces/{ws}/automation/rules`

```json
{
  "name": "File invoices",
  "trigger": { "type": "itemAdded", "list": "Invoices", "contentType": "Invoice" },
  "condition": "fields/amount gt 100",
  "actions": [
    { "type": "item.file", "inputs": { "folder": "{created:yyyy}/{counterparty}", "title": "{counterparty} {invoiceNumber}" } },
    { "type": "task.create", "inputs": { "list": "Tasks", "title": "Check {title}", "assignedTo": ["group:Accountants"], "dueInDays": 3 } },
    { "type": "workflow.start", "inputs": { "workflow": "Invoice approval" } }
  ]
}
```

- **Trigger:**
  - `type` is `itemAdded`, `itemUpdated`, `itemDeleted`, or an extension
    trigger (`GET /v1.0/automation/triggers`).
  - `list`, `contentType` (name or key) and, for updates, `changedFields` narrow
    it down.
- **Condition:** an OData filter on the item, as in the items API. It needs the
  trigger's `list`.
- **Actions** run in order, on behalf of the organization. The first failure
  stops the rule.
- **Runs:** each rule runs once per event. See
  `GET …/rules/{id}/runs` (`completed`, `skipped`, `failed` with the error).
- **Loops:** changes made by automation trigger rules again up to a depth of 3,
  then stop. A rule that updates its own item cannot loop forever.

## Actions

| Action | Inputs |
|---|---|
| `item.update` | `fields`: values to set; text may contain tokens |
| `item.file` | `folder`: path template (each level becomes a folder; missing ones are created); `title`: new title |
| `task.create` | `list` (a task list), `title`, `assignedTo`, `dueInDays`, `priority`, `description` |
| `notify` | `to`, `title`, `body`; the notification links to the item |
| `workflow.start` | `workflow`: name of a workflow in the workspace |
| `{extension}.…` | Actions of enabled extensions (`GET /v1.0/automation/actions`) |

**Tokens** in text inputs:

- `{title}`, `{fieldName}`, `{fieldName:format}` (dates and numbers);
- `{created:yyyy}`, `{modified}`, `{today:yyyy-MM-dd}`;
- `{list}`, `{id}`;
- `{outcome:Step}` (workflows);
- `{data:name}` (extension trigger data).

Term values become term names and person values become user names. `{{` and
`}}` are literal braces.

**People** (`to`, `assignedTo`, `assignees`, `escalateTo`):

- user names;
- `group:Name` (its members);
- `field:name` (a person field of the item);
- `creator` (of the item);
- `actor` (the user whose change started the automation).

## Workflows

`POST /v1.0/workspaces/{ws}/automation/workflows`

```json
{
  "name": "Invoice approval",
  "steps": [
    { "type": "approval", "name": "Manager", "assignees": ["field:approver"], "title": "Approve {title}",
      "dueInHours": 48, "escalateTo": ["group:Finance leads"] },
    { "type": "condition", "step": "Manager", "is": "approved",
      "then": [ { "type": "action", "action": "item.update", "inputs": { "fields": { "status": "approved" } } } ],
      "else": [ { "type": "action", "action": "notify", "inputs": { "to": ["creator"], "title": "{title} was rejected" } } ] },
    { "type": "delay", "hours": 24 },
    { "type": "action", "action": "notify", "inputs": { "to": ["creator"], "title": "{title}: {outcome:Manager}" } }
  ]
}
```

**Step types:**

- `approval`: waits until one of the assignees decides `approved` or
  `rejected`.
  - Overdue requests are escalated: the `escalateTo` people are added as
    assignees and everyone is notified.
  - Needs a `name`: later conditions and tokens refer to it.
- `condition`: either `step` + `is` (an approval outcome) or `filter` (OData
  on the item), then `then` / `else` steps.
- `delay`: waits `hours`.
- `action`: runs an action with `inputs`.

**Running workflows:**

- Start a workflow on an item with `POST …/lists/{list}/items/{item}/workflows`
  `{ "workflow": "Invoice approval" }`, or from a rule with `workflow.start`.
- Runs: `GET …/automation/runs?itemId=` shows the status (`running`,
  `waiting`, `completed`, `failed`, `cancelled`), the approval outcomes and a
  log. Cancel with `POST …/runs/{id}/cancel`.
- Changing the steps creates a new version. Running runs keep the version they
  started with.

**Approvals:**

- `GET /v1.0/me/approvals` lists your pending approvals (`?status=approved`,
  `rejected`, `cancelled`).
- Decide with `POST /v1.0/me/approvals/{id}/decision`
  `{ "outcome": "approved", "comment": "…" }`.

## Extensions

Extensions add actions with `builder.AddAutomationAction<T>()` (implement
`IAutomationAction`). They add triggers with
`builder.AddAutomationTrigger(new(key, description))` and raise them with
`IAutomationTriggers.RaiseAsync`. Keys start with the extension id. Both only
work in tenants where the extension is enabled. See `docs/extensions.md` and
the sample `samples.invoices` (trigger `approvalNeeded`, action `approve`).
