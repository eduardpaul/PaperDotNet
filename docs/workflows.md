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
  - `manual`: a person starts it (see below);
  - `itemAdded`, `itemUpdated`, `itemDeleted` (moved to the recycle bin),
    `itemRestored`;
  - `schedule`: on `cron` (5 fields: minute hour day month weekday, e.g.
    `0 8 * * 1-5`) in `timeZone` (default: the organization's). The run has
    no item; `{trigger:occurrence}` is the time it is for.
  - `date`: for each item of `list` when its date `field` plus `offsetHours`
    (negative: before) is reached, e.g. a day before the due date
    (`"field": "dueDate", "offsetHours": -24`). Dates without a time count
    from midnight in the organization's time zone. `{trigger:date}` is the
    date.
  - `document.processed`: a document's file was processed, so its text
    exists (data: `version`, `pageCount`, `ocr`). Use it instead of
    `itemAdded` for anything that depends on the text.
  - `task.completed` (data: `completedBy`), `comment.added` (data:
    `commentId`, `text`, `author`, `reply`), `approval.decided` (data:
    `workflow`, `step`, `outcome`, `comment`, `decidedBy`);
  - or an extension trigger.
- `list`, `contentType` (name or key) and, for updates, `changedFields`
  narrow it down. `terms` (term paths `Group/Set/Term`) needs the item to
  have one of these terms, or a term below one, in any field. Folders never
  trigger workflows.
- **Timed triggers** start once per occurrence (`schedule`) or once per item
  and date value (`date`), checked every minute. Only moments after the
  workflow was saved or turned on count: a new workflow does not run for
  dates in the past, and occurrences missed while the server was down run
  once.

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
  `POST …/lists/{list}/items/{item}/workflows` `{ "workflow": "Invoice approval" }`,
  or on several items (up to 100) with
  `POST …/workflows/{id}/runs` `{ "listId": …, "itemIds": [ … ] }`.
  - You need Contribute access to the items.
  - The items must match the trigger's list and content type, and the
    condition; all are checked before anything starts.
  - A `manual` workflow whose trigger has no list can also start without an
    item: `POST …/workflows/{id}/runs` `{}` (Contribute on the workspace).
  - The trigger's `inputs` describe what a person gives when starting it
    (a JSON Schema object: `properties` with a `type` each, `required`).
    Starts send `"inputs": { … }`; they are checked and become the run's
    variables (`{var:label}`).
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
| `{extension}.…` | Actions of enabled extensions |

`GET /v1.0/workflows/activities` lists every activity: the flow activities
(`kind: flow`) and the actions (`kind: action`), each with its `ports`, an
`inputSchema` and an `outputSchema` (JSON Schema) for forms and tools.

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

## AI activities

With a chat model configured, workflows can read documents and items with AI
([ADR-0036](adr/0036-workflows-as-the-core.md); off by default):

```bash
PAPERDOTNET__AI__Chat__Provider=openai            # any OpenAI-compatible API
PAPERDOTNET__AI__Chat__Endpoint=http://ollama:11434/v1   # or https://api.openai.com/v1, Azure OpenAI …/openai/v1/
PAPERDOTNET__AI__Chat__Model=llama3.1
PAPERDOTNET__AI__Chat__ApiKey=…                   # if the service needs one
PAPERDOTNET__AI__Chat__DailyTokens=200000         # per organization and UTC day; 0 = no limit
```

| Activity | Inputs | Output and ports |
|---|---|---|
| `ai.extract` | `fields` (default: all it can fill: text, note, email, url, number, currency, boolean, date, dateTime, choice), `instructions`, `mode` (`apply` or `suggest`), `minConfidence` (default 0.7) | `values`, `confidence`, `applied`, `uncertain`; port `lowConfidence` when a field stays empty or uncertain |
| `ai.classify` | `termSet` (`Group/Set`), `field` (a managed metadata field to set), `instructions`, `mode`, `minConfidence` | `term`, `termId`, `confidence`, `applied`; port `lowConfidence` when no term fits well enough |
| `ai.summarize` | `maxWords` (default 80), `field` (a text field to write it to), `instructions` | `summary` |
| `ai.prompt` | `prompt`, `system` (templates), `includeContent`, `schema` (JSON Schema for structured output) | `text`, or `json` with a schema |

- The model reads the item's values and the text of its document (the same
  text search uses), up to `AI:Chat:MaxInputCharacters` (24000).
- With `mode: apply` (default) values with at least `minConfidence` are
  written to the item; `suggest` only puts them in the node's output for
  later nodes (for example a review task).
- **Cache:** the same model and input reuse the earlier answer for
  `AI:Chat:CacheDays` (30) days, without calling the model.
- **Budget and record:** every call is recorded with its workflow run, model,
  tokens and a hash of what was sent (not the text). When the organization's
  `DailyTokens` are used up, AI activities fail; retry the runs later
  (`…/runs/{id}/retry`) or give the node a `retry` policy.
- Without a configured model, AI activities fail with a clear error.

### Batched AI

An AI activity with `"execution": "batch"` does not call the model right away
(AI-08). Batching is built from workflow parts only; there are no tables of its
own:

1. The step **waits** on a wait of kind `ai.batch`, with its question as the
   wait's data. The run holds no server while it waits.
2. The workspace's **batch workflow** answers the waiting questions on its
   schedule. This is the built-in workflow "AI batch": a `schedule` trigger and
   the activity `ai.batch`.
3. The step **runs again** with the answer and continues.

| Input | Meaning |
|---|---|
| `execution` | `immediate` (call now) or `batch`; the default is `AI:Batch:Execution` (`immediate`) |
| `deadlineHours` | the longest the step waits (1 to 336); the default is `AI:Batch:DeadlineHours` (48) |
| `onDeadline` | when the deadline passes without an answer: `immediate` (default: call the model now) or `fail` |

Turn on "AI batch" in the workspace's workflow settings. Its parameters:

- `schedule`: a cron expression, default `0 1 * * *`, or `0 1,13 * * *` for
  twice a day.
- `timeZone`.
- `pollMinutes`: default 5.
- `maxQuestions`: default 2000 per run.

`PAPERDOTNET__AI__Batch__Execution=batch` makes batch the default for AI
activities.

- **No batch workflow, no waiting.** If no enabled workflow of the workspace
  uses `ai.batch`, a batched step asks at once, so it never waits for nothing.
- **Shared questions.** Steps that ask the same question (same model and
  input) are answered with one call.
- **Cache first.** A question answered before (within `CacheDays`) is
  answered right away, without waiting.
- **Without a batch API**, `ai.batch` asks the chat model each question once,
  within the daily budget. This is still useful: the calls happen outside
  working hours, and duplicates are asked once.
- **With a batch API**, a server registers an `IAiBatchClient`
  (Workflows.Contracts), for example for the Azure OpenAI or OpenAI Batch API.
  `ai.batch` then sends one batch per model and waits: it checks every
  `pollMinutes` with a run-again wait that keeps the provider's batch ids as
  its data. When a batch has finished, it records each answer's tokens and
  lets the steps go on. Questions a failed or expired batch did not answer are
  given back for the next run, until their deadline.
- **Crash safety.** A batch run first takes its questions: it marks them in
  their waits' data, so two runs never send the same. Each batch is tagged with
  an id derived from the step's execution id. When the step runs again after a
  failure (its retry policy) or a crash, it finds its batch at the provider
  (`FindAsync`) instead of sending it twice.
- While the day's budget is used up, nothing is sent. The questions wait for
  the next run or their deadline.
- Batches are per workspace. Batching across the whole organization comes
  with organization workflows.

Example: read receipts and send uncertain ones to review.

```json
"flow": {
  "start": "extract",
  "nodes": {
    "extract": { "activity": "ai.extract",
                 "inputs": { "fields": ["store", "purchaseDate", "total"], "minConfidence": 0.8 },
                 "next": { "done": "classify", "lowConfidence": "review" } },
    "review":  { "activity": "task.create", "inputs": { "list": "Tasks", "title": "Check {title}: {step:extract.uncertain}" },
                 "next": { "done": "classify" } },
    "classify": { "activity": "ai.classify", "inputs": { "termSet": "Documents/Kinds", "field": "kind" } }
  }
}
```

## Built-in workflows

The product ships ready-made workflows (EVT-12,
[ADR-0036](adr/0036-workflows-as-the-core.md)). They are listed under
**Built-in workflows** in the workspace's workflow settings, and at
`GET /v1.0/workspaces/{ws}/workflows/builtIns`.

| Key | Name | Parameters | Needs |
|---|---|---|---|
| `workflows.approveItems` | Approve new items | `list`, `approvers` (required); `statusField` (`status`), `approvedValue` (`Approved`), `rejectedValue` (`Rejected`), `dueInHours` | |
| `documents.classify` | Classify new documents | `termSet` (`Group/Set`), `field` (required); `minConfidence` (0.7), `library`, `execution` | AI |
| `documents.extract` | Extract fields | `fields`, `minConfidence` (0.7), `library`, `execution` | AI |
| `ai.batchWindow` | AI batch | `schedule` (`0 1 * * *`), `timeZone`, `pollMinutes` (5), `maxQuestions` | AI |

- **Turn on or off** per workspace with its parameters:
  `PUT …/workflows/builtIns/{key}` `{ "enabled": true, "parameters": { … } }`.
  The values are checked like a saved workflow (the list must exist, and so
  on). Turning it off keeps them.
- Once on, it is listed with the workspace's workflows (`builtIn` holds its
  key), runs like any workflow, and cannot be edited.
- **Copy to change:** `POST …/workflows/builtIns/{key}/copy` `{ "name": … }`
  creates a workflow of the workspace from it (`copiedFrom` holds the key),
  with its current settings, and turns the built-in one off there.
- **Updates:** when a new release changes a built-in definition, an hourly job
  saves it as a new version of each workspace's copy (running runs keep
  theirs). A built-in workflow that the release no longer has is turned off.
- `library` narrows the document workflows to one library (default: all
  libraries of the workspace), and `execution: batch` sends their AI calls in
  the batch window.

Modules ship built-in workflows with `services.AddWorkflow(…)`, and extensions
with `builder.AddWorkflow(…)`: a `BuiltInWorkflow` with a key, a name, a
definition as in the API, and parameters as a JSON Schema. An extension's
workflows are offered only where the extension is enabled. In the definition, a string that is exactly
`{param:name}` is replaced by the value (any JSON). Inside a longer string or
a property name, `{param:name}` is replaced by its text.

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
- A built-in workflow is exported as its key (`BuiltIn`) with its parameter
  values, so the target uses its own release's definition. A copy keeps
  `CopiedFrom`. A key the target does not have fails the dry run.
- Runs, approvals and waits are never exported.
- Templates made before the rename (section `Automations` in
  `urn:paperdotnet:automation:2`) are still read.

## Extensions

The engine runs workflows. Most of what they do comes from modules built on
the extension SDK, through the same extension points an extension uses:

| Module | Adds |
|---|---|
| Workflows (engine) | flow activities (`approval`, `delay`, `if`, …), `item.update`, `item.file`, "Approve new items" |
| Tasks | `task.create`, trigger `task.completed` |
| Notifications | `notify` |
| AiWorkflows | `ai.extract`, `ai.classify`, `ai.summarize`, `ai.prompt`, `ai.batch`, "AI batch" |
| Documents | trigger `document.processed`, "Classify new documents", "Extract fields" |
| Collaboration | trigger `comment.added` |

Modules register with `services.AddWorkflowActivity<T>()`,
`services.AddWorkflowTrigger(…)` and `services.AddWorkflow(…)`. Extensions use
the same calls on the builder (`builder.AddWorkflowActivity<T>()`, and so on).
Their keys start with the extension id, and they only work in organizations
where the extension is enabled. See [extensions.md](extensions.md).

Actions must be safe to run again: the same step can run again after a crash
with the same `context.ExecutionKey` and `context.ExecutionId`. Use the id as
the id of what the action creates (for example
`IListItemStore.CreateAsync(workspaceId, listId, context.ExecutionId, …)`),
and the key to find what an earlier attempt did or as a deduplication key.

An activity can describe itself (`InputSchema`, `OutputSchema`) and offer
more outcome ports than `done` and `error` (`Outcomes`; return
`WorkflowActivityResult.Ok("approved", output)` to take one).

**Waiting for something else** ([ADR-0036](adr/0036-workflows-as-the-core.md)):
an activity returns `WorkflowActivityResult.Wait(kind, key, resumeAt)`. The run
stops (it holds no server) until someone completes that wait:

```csharp
await bookmarks.CompleteAsync(kind, key, new JsonObject { ["outcome"] = "paid" }, ct); // IWorkflowBookmarks
```

- The payload becomes the node's output, and its `outcome` picks the port
  (`done` when that port is not connected).
- Completing is saved together with the message that resumes the run, and
  completing again does nothing, so call it after saving your own state and
  again after a crash.
- A completion that arrives before the run has started waiting is kept, and
  the run continues right away when it gets there.
- When `resumeAt` passes first, the wait times out: the payload is
  `{ "outcome": "timeout" }`.
- Extension waits have kinds that start with the extension id; `approval`,
  `delay` and `retry` belong to the engine. The key identifies the wait within
  the kind (for example a request id), unique per tenant.

This is how long-running work outside the engine joins a workflow, for example
a daily batch of AI requests.

A wait can carry data (`Wait(kind, key, resumeAt, data)`): what it is about,
as JSON, for whoever completes it.

An activity that finishes the work itself returns
`WorkflowActivityResult.WaitAndRunAgain(kind, key, resumeAt, data)` instead.
When the wait is completed (or `resumeAt` passes), the node runs again with the
same `ExecutionId`. It gets the wait back as `context.Resumed`, with its data
and the completion's payload. This is also how an activity keeps state between
polls: it waits again with the same key and new data. Batched AI works this
way.

See `docs/extensions.md` and the sample `samples.invoices` (trigger
`approvalNeeded`, actions `approve` and `awaitPayment`, which waits until the
invoice is paid or times out).
