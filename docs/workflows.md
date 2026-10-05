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
    Use `parameters.when` on `itemAdded` or `itemUpdated` to filter field values,
    changes and tag additions or removals (see [parameterized item triggers](#parameterized-item-triggers)).
  - `webhook`: an authenticated request launches a workspace workflow;
  - `schedule`: on `cron` (5 fields: minute hour day month weekday, e.g.
    `0 8 * * 1-5`) in `timeZone` (default: the organization's). The run has
    no item; `{trigger:occurrence}` is the time it is for.
  - `date`: for each item of `list` when its date `field` plus `offsetHours`
    (negative: before) is reached, e.g. a day before the due date
    (`"field": "dueDate", "offsetHours": -24`). Dates without a time count
    from midnight in the organization's time zone. `{trigger:date}` is the
    date.
  - `document.added`: a file was added to a library, a new document or a new
    version (data: `version`, `mediaType`, `fileName`, `newDocument`). An
    upload only stores the file: reading its text, thumbnails, page images and
    OCR are workflows of the library ([documents.md](documents.md)). For
    anything that needs the text, use `wf.documents.text.hasText`.
  - `wf.{key}.{event}`: an event of another workflow (see
    [Workflow events](#workflow-events)).
  - `task.completed` (data: `completedBy`), `comment.added` (data:
    `commentId`, `text`, `author`, `reply`), `approval.decided` (data:
    `workflow`, `step`, `outcome`, `comment`, `decidedBy`);
  - or an extension trigger.
- `list`, `contentType` (name or key) and, for updates, `changedFields`
  narrow it down. `data` (module and extension triggers) needs the trigger's
  data to have these values, e.g. `"data": { "newDocument": true }` on
  `document.added`. `terms` (term paths `Group/Set/Term`) needs the item to
  have one of these terms, or a term below one, in any field. Folders never
  trigger workflows.
- Item-created and item-updated triggers accept `parameters.when`: typed field
  comparisons, tag/collection additions and removals, transitions, and nested
  AND/OR groups. They evaluate the values captured transactionally with the event,
  including when two quick edits are delivered later or out of order. Content-type
  and legacy term filters use these snapshots when available. Module and legacy
  queued events without snapshots retain their existing live-value filters.
- The existing workflow-level OData `condition` reads the current item when
  handled. It remains supported as an additional AND guard; see
  [how the two condition mechanisms overlap](#trigger-conditions-and-workflow-level-odata).
- **Timed triggers** start once per occurrence (`schedule`) or once per item
  and date value (`date`), checked every minute. Only moments after the
  workflow was saved or turned on count: a new workflow does not run for
  dates in the past, and occurrences missed while the server was down run
  once.

**Several triggers:** `triggers` instead of `trigger` lists up to 10 of
them, and any of them starts a run. One event starts one run, even when
several triggers match it. Each trigger is checked on its own: a schedule or
date trigger keeps its own state, and a manual start uses the `manual` one.
For example, the receipts package reads a receipt when it is tagged, and
when a file uploaded with the tag is processed:

```json
"triggers": [
  { "type": "itemUpdated", "list": "Receipts", "changedFields": ["tags"], "terms": ["Receipts/Tags/ticket"] },
  { "type": "document.added", "list": "Receipts", "terms": ["Receipts/Tags/ticket"] }
]
```

**Condition:** an OData filter on the item, as in the items API. It needs the
trigger's `list` (every trigger's, with several) and queries the current item when
the event is processed. An item that does not match starts nothing. A condition that
can no longer be checked (for example, a field was removed) gives a failed run
with the error. This supported workflow-level guard is optional and differs from
the event-snapshot checks in `parameters.when`.

**Steps** run in order, on behalf of the organization:

- `action`: runs an action with `inputs`. A failure fails the run.
- `approval`: waits until one of the `assignees` decides `approved` or
  `rejected`.
  - Overdue requests (`dueInHours`) are escalated: the `escalateTo` people are
    added as assignees and everyone is notified.
  - Needs a `name`: later conditions and tokens refer to it.
  - Optional `inputSchema` collects information with the decision; see
    [Approval forms](#approval-forms).
- `condition`: either `step` + `is` (an approval outcome) or `filter` (OData
  on the item), then `then` / `else` steps.
- `delay`: waits `hours` (the run continues within a minute of the time).

Approval steps and `filter` conditions need an item. Extension triggers without
an item fail there.

Changing a workflow creates a new version. Running runs keep the version
they started with.

### Workflow events

Workflows follow each other by events ([ADR-0038](adr/0038-documents-composed-from-workflows.md)):
- Every workflow has a **`key`**, the name of its events. It is made from the
  name when the workflow is created (`Check big bills` → `check-big-bills`),
  or given as `key` (lower case letters, digits, dashes, underscores; unique
  in the workspace). A rename does not change it; a built-in workflow's key is
  its built-in key.
- When a run ends, the workflow raises **`wf.{key}.completed`** or
  **`wf.{key}.failed`** (data: `runId`, `status`, and `error`).
- A flow's **`event.raise`** node raises **`wf.{key}.{event}`** with `data`
  (strings may be tokens; one token keeps its type):
  `{ "activity": "event.raise", "inputs": { "event": "ready", "data": { "total": "{amount}" } } }`.
- The events carry the run's item. Use them as triggers with the usual filters:
  `{ "type": "wf.check-big-bills.ready", "list": "Bills", "data": { "total": 100 } }`.
- Each workflow in a chain counts as one step of the loop protection (below).

**Concurrency** (`concurrency`) decides what happens when the workflow starts
on an item that it is still running on:
- `parallel` (default): both runs go on.
- `skip`: the new run does not start (a manual start answers 409).
- `replace`: the running one is cancelled.

It is checked when a run starts and again when it begins, so two runs started
at the same moment still end up as one.

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
  - `forEach`: runs the nodes on its `item` port once per element, with the
    element in the variable `as` (default `item`: `{var:item.name}`); the last
    of those nodes leads back to the `forEach` node. The elements are `items`
    (an array, or one token such as `{step:read.json.lines}`) or the items a
    `query` finds (`list` by name, `filter` in OData with tokens; each element
    is the item's fields and its `id`). At most 500 elements. Then port `done`;
    its output is `index` and `count`. A loop inside a loop starts again for
    each element of the outer one, and nested loops need different `as`
    names;
  - `script` (`code`): JavaScript for the data work, see
    [Scripts](#scripts): ports `done` and `error`;
  - `event.raise` (`event`, `data`): raises `wf.{key}.{event}` for other
    workflows, see [Workflow events](#workflow-events): port `done`;
  - `end` ends the run; `fail` (`message`) ends it as failed.
- **Outputs and variables:** each node's result is its output (an action's
  output, an approval's `outcome`, `decidedBy` and `comment`, or `error` on the
  `error` port). `variables` gives the initial values of the run's variables.
- **Failures:** a failing node is tried again by its `retry` policy
  (`attempts` up to 10, `delayMinutes`, default 1), then continues on its
  `error` port, and otherwise fails the run at that node.
- **Checks:** the start and every next node must exist, ports must fit their
  activity, every node must be reachable from the start, and a flow has at
  most 100 nodes. A run executes at most 10,000 nodes. After 500 nodes in one
  go it saves and continues in a new message, so long loops do not hold a
  server.

**Mapping data.** Simple mappings need no code:
- `item.update` sets the item's fields;
- `forEach` goes over an array;
- `item.create` makes one item per element.

For a field that is not text, a value that is exactly one token keeps its
type, so `"total": "{step:read.json.total}"` writes a number. For more (several
items, arithmetic, reshaping) use a script.

### Scripts

The graph orchestrates; a `script` node does the data work
([ADR-0037](adr/0037-scripts-in-workflows.md)). It runs JavaScript (strict
mode) in a sandbox on the server. The code is `code`, a string or an array of
lines. It is checked for syntax when the workflow is saved, and it runs as the
body of an async function: `await` the `items` calls, and `return` the node's
`result`.

The receipts package ([samples/receipts-package](../samples/receipts-package/README.md))
saves the AI's answer with one script node:

```js
const receipt = steps.read.json;
await items.update(item.list, item.id, { store: receipt.store, total: receipt.total, status: 'Read' });
let cursor;
do {
  const page = await items.related(item.id, { type: 'contains receipt line', direction: 'outgoing', cursor });
  for (const edge of page.value) {
    await items.unrelate(item.id, edge.id);
    await items.deleteById(edge.item.id);
  }
  cursor = page.nextCursor;
} while (cursor);
for (const line of receipt.lines) {
  const id = await items.create('Receipt lines', { title: line.description, amount: line.amount });
  await items.relate(item.id, id, 'contains receipt line');
}
return { lines: receipt.lines.length };
```

**What a script sees.** All values are JSON copies; scripts have no access to
.NET, files, the network or timers.
- `item`: the run's item (its fields, `id`, and `list`, the list's name), or null.
- `vars`: the run's variables. Changes to its properties are kept.
- `steps`: the outputs of earlier nodes, by node id.
- `trigger`: the trigger's data.
- `items`: the lists of the workspace, by name:
  - `get(list, id)` gives the item, or null;
  - `query(list, { filter, orderBy, top })` uses OData as in the items API
    (default 100 items, at most 1000);
  - `create(list, fields)` gives the new item's id;
  - `update(list, id, fields)` merges the fields (null removes a value);
  - `delete(list, id)` moves the item to the recycle bin;
  - `related(id, { type, direction, top, cursor })` reads a page of global graph edges (`value`, `nextCursor`), with a readable peer in each edge's `item`;
  - `relate(id, otherId, type?)` plans an idempotent link; direction defaults to the predicate;
  - `unrelate(id, relationshipId)` plans removal of one edge from either endpoint;
  - `deleteById(id)` plans deletion using the item's current list and workspace.
- `log(text)`: adds a line to the run's log.

**Writes are planned, then applied.**
- Reads happen right away. Item and relationship writes only add to a plan,
  so a read does not see them.
- When the script has returned, the plan is saved with the run and applied in
  order through the normal write path (validation, events, search).
- A crash or retry continues the saved plan and never runs the script twice.
  Created items get ids derived from the step, so repeating a create creates
  nothing.
- A write that fails stops the node there; a retry continues from it.

**Failures.** A failed `items` call fails the node even when the script does
not await it or catches it: the plan would be incomplete. A failed script
writes nothing and continues on the `error` port (its message has the script
line, e.g. `boom (line 2)`).

**Limits** (server settings `Workflows:Scripts`):
- 2 seconds, 32 MB, 1,000,000 statements, recursion depth 100;
- 200 reads and 1000 writes per run of the node;
- 50,000 characters of code.

**The same API in the SDK.** The TypeScript SDK (`@paperdotnet/client`)
defines this API with its types (`ScriptGlobals`; `scriptDeclarations` for
editors). `runWorkflowScript(client, { workspaceId, code, item, vars, apply })`
runs a script in Node against a real server, as the caller and without a
sandbox, to write and test it (`apply: false` only plans the writes). Shared
contract tests (`sdk/typescript/test/scripts.test.mjs`) run every case on both
and expect the same result, variables, log, errors and writes.

The web editor edits steps; flows are edited in its JSON view for now.

## Runs

- **When runs start:** every trigger that matches starts one run, and the same
  event never starts a second run of the same workflow.
- **Manual start:** start a `manual` workflow on an item with
  `POST …/lists/{list}/items/{item}/workflows` `{ "workflow": "Invoice approval" }`,
  or on several items (up to 100) with
  `POST …/workflows/{id}/runs` `{ "listId": …, "itemIds": [ … ] }`.
  - You need Contribute access to the items.
  - Library built-ins that advertise `allowManualLaunch: true` can run even
    with automatic processing off, using
    `POST …/lists/{listId}/workflows/builtIns/{key}/runs` `{ "itemIds": [ … ] }`.
    This creates their workflow row if needed without enabling automatic runs,
    and preserves existing parameter values. The extension must be enabled.
    The web app offers these workflows from a file's Preview tab (including
    Inbox) and the library selection toolbar.
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
- **Loops:** changes made by workflows, and events of workflows
  (`wf.{key}.…`), trigger workflows again up to a depth of 5, then stop. A
  workflow that updates its own item cannot loop forever.
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
| `item.update` | `fields`: values to set; text may contain tokens (a single token keeps its type, see below). With `list` and `id`, another item |
| `item.get` | `list`, `id`: the item's fields (and `id`) as output |
| `items.query` | `list`, `filter` (OData, with tokens), `top`: output `items` |
| `item.create` | `list` (by name), `fields` (as `item.update`), `contentType`; output `itemId`. Safe to repeat: the item's id is the step's execution id |
| `item.delete` | `list`, `id` (e.g. `{var:line.id}`): to the recycle bin; an item already gone is not an error |
| `item.file` | `folder`: path template (each level becomes a folder; missing ones are created); `title`: new title |
| `task.create` | `list` (a task list), `title`, `assignedTo`, `dueInDays`, `priority`, `description` |
| `notify` | `to`, `title`, `body`; the notification links to the item |
| `{extension}.…` | Actions of enabled extensions |

`GET /v1.0/workflows/activities` lists every activity: the flow activities
(`kind: flow`) and the actions (`kind: action`), each with its `ports`, an
`inputSchema` and an `outputSchema` (JSON Schema) for forms and tools.

**Tokens** in text inputs:

- `{title}`, `{fieldName}`, `{fieldName:format}` (dates and numbers);
  `{item:fieldName}` is always the item's field, even one called `id` or
  `list`;
- `{created:yyyy}`, `{modified}`, `{today:yyyy-MM-dd}`;
- `{list}`, `{id}`;
- `{outcome:Step}` (approval outcomes);
- `{var:name}` or `{var:name.path}` (variables), `{step:Node.path}` (a value in
  a node's output, e.g. `{step:task.taskId}`);
- `{trigger:name}` or `{data:name}` (extension trigger data).

Term values become term names and person values become user names. `{{` and
`}}` are literal braces.

**Typed values:** in the `fields` of `item.update` and `item.create`, a value
for a field that is not text (number, date, lookup, person, term, …) that is
exactly one token without a format gets the value with its type: a number
stays a number, a list a list, and terms and people stay ids. Text fields
(text, note, email, url, choice) always get the text.

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
| `ai.prompt` | `prompt`, `system` (templates), `includeContent`, `schema` (JSON Schema for structured output), `includeImages` | `text`, or `json` with a schema |

- The model reads the item's values and the text of its document (the same
  text search uses), up to `AI:Chat:MaxInputCharacters` (24000).
- **Images:** `includeImages` (every AI activity) also sends the item's pages
  as images: `true` for the first 3 pages, or a number of pages (up to 10).
  Use it with a model that reads images (such as gpt-4.1 or gpt-4o) when the
  text is not enough, for example photos of receipts, where OCR often loses
  the prices. Documents renders the pages (JPEG, 1600 pixels wide, cached with
  the previews); the images are part of the cache key. Batched steps keep only
  which pages to send, and the images are loaded when the batch is sent.
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
- **With a batch API** (`AI:Batch:Provider=openai`, below, or any other
  `IAiBatchClient` of Workflows.Contracts), `ai.batch` sends one batch per model
  and waits: it checks every
  `pollMinutes` with a run-again wait that keeps the provider's batch ids as
  its data. When a batch has finished, it records each answer's tokens and
  lets the steps go on. Questions a failed or expired batch did not answer are
  given back for the next run, until their deadline.
- **Crash safety.** A batch run first takes its questions: it marks them in
  their waits' data, so two runs never send the same. Each batch is tagged with
  an id derived from the step's execution id. When the step runs again after a
  failure (its retry policy) or a crash, it finds its batch at the provider
  (`FindAsync`) instead of sending it twice.
- **The OpenAI Batch API** works with OpenAI, Azure OpenAI and Azure AI
  Foundry (`…/openai/v1/`). It uses the chat model's endpoint and key unless
  `AI:Batch:Endpoint` and `AI:Batch:ApiKey` are set; the model is `AI:Chat:Model`
  (on Azure, the name of a deployment of type *Global Batch*, which answers
  only batches):

  ```bash
  PAPERDOTNET__AI__Chat__Provider=openai
  PAPERDOTNET__AI__Chat__Endpoint=https://<resource>.services.ai.azure.com/openai/v1/
  PAPERDOTNET__AI__Chat__Model=gpt-4.1               # the deployment
  PAPERDOTNET__AI__Chat__ApiKey=…
  PAPERDOTNET__AI__Batch__Provider=openai
  PAPERDOTNET__AI__Batch__Execution=batch            # optional: batch by default
  ```

  Each batch is a JSONL file of chat completion requests (with the JSON schema
  and images of the step), tagged with its id in the batch's metadata, and the
  answers are read from its output and error files. A batch-only deployment
  cannot answer right away, so give batched steps `"onDeadline": "fail"`.
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

A complete, importable example is the receipts package
([samples/receipts-package](../samples/receipts-package/README.md)): a tagged
photo or PDF of a receipt is read in the batch window, with its images, into
the receipt's fields and one item per line.

## Built-in workflows

The product ships ready-made workflows (EVT-12,
[ADR-0036](adr/0036-workflows-as-the-core.md)). They are listed under
**Built-in workflows** in the workspace's workflow settings, and at
`GET /v1.0/workspaces/{ws}/workflows/builtIns`.

| Key | Name | Parameters | Needs |
|---|---|---|---|
| `workflows.approveItems` | Approve new items | `list`, `approvers` (required); `statusField` (`status`), `approvedValue` (`Approved`), `rejectedValue` (`Rejected`), `dueInHours` | |
| `documents.text` | Read the text (per library, on) | | |
| `documents.thumbnail` | Make thumbnails (per library, on) | | |
| `documents.pages` | Render pages (per library, on) | | |
| `documents.ocr` | Recognize text (per library, off) | `languages` | |
| `documents.classify` | Classify new documents | `termSet` (`Group/Set`), `field` (required); `minConfidence` (0.7), `library`, `execution` | AI |
| `documents.extract` | Extract fields | `fields`, `minConfidence` (0.7), `library`, `execution` | AI |
| `ai.batchWindow` | AI batch | `schedule` (`0 1 * * *`), `timeZone`, `pollMinutes` (5), `maxQuestions` | AI |

- **Turn on or off** per workspace with its parameters:
  `PUT …/workflows/builtIns/{key}` `{ "enabled": true, "parameters": { … } }`.
  The values are checked like a saved workflow (the list must exist, and so
  on). Turning it off keeps them, and works even when the server no longer has
  what it needs. Once it was turned on, changes need `If-Match` with its ETag
  (`@odata.etag` in the list).
- Once on, it is listed with the workspace's workflows (`builtIn` holds its
  key), runs like any workflow, and cannot be edited.
- **Copy to change:** `POST …/workflows/builtIns/{key}/copy`
  `{ "name": …, "parameters": { … } }` creates a workflow of the workspace
  from it (`copiedFrom` holds the key), with the given settings (default: its
  current ones), and turns the built-in one off there.
- **Updates:** when a new release changes a built-in definition, an hourly job
  saves it as a new version of each workspace's copy (running runs keep
  theirs). A built-in workflow that the release no longer has is turned off.
- **Per library:** the document workflows "Read the text", "Make thumbnails",
  "Render pages" and "Recognize text" are turned on per library
  (`GET`/`PUT …/lists/{listId}/workflows/builtIns[/{key}]`). Each library has
  its own row, named with the library (`Read the text (Invoices)`), whose
  triggers apply to it only. Those on by default are created the first time a
  library needs them; turned off, they stay off. See [documents.md](documents.md).
- `library` narrows "Classify" and "Extract" to one library (default: all
  libraries of the workspace), and `execution: batch` sends their AI calls in
  the batch window. They start once a document's text was read
  (`wf.documents.text.hasText`).

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
  `CopiedFrom`. A key the target does not have fails the dry run, unless the
  workflow was turned off (it is skipped with a warning). One that needs what
  the target server lacks (such as AI) is set up turned off, with a warning.
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
| Documents | trigger `document.added`; `document.readText`, `document.thumbnail`, `document.renderPages`, `document.ocr`; per library "Read the text", "Make thumbnails", "Render pages", "Recognize text"; "Classify new documents", "Extract fields" |
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


Relationship scripts also support a workspace graph query and edge attributes (ADR-0041):

```js
const page = await items.relationships({ type: 'references', filter: 'attributes/confidence le 0.7', top: 100 });
for (const edge of page.value) {
  await items.updateRelationship(edge.sourceItem.id, edge.id, { reviewed: true }, edge.version);
}
// Follow page.nextCursor with the same query to read further pages.
```

Each result has both `sourceItem` and `targetItem`, plus `id`, `type`, `directed`, `attributes` and `version`.
`items.related` includes attributes/version too. Create initial attributes with
`items.relate(sourceId, targetId, type, { confidence: 0.6 })`. Repeated linking preserves the existing bag; explicit
`items.updateRelationship` patches it and null removes a key. Updates require the read edge version and are planned,
so further reads in the same script still see the old attributes. Workflows execute with their existing system access;
SDK scripts execute with the caller's permissions. Item fields and edge attributes have separate versions.

## On-demand and workspace workflows

`scope` is `workspace` or `list`. Omit it for existing trigger-based behavior.
Workspace workflows accept `manual`, `schedule`, and `webhook` triggers without
an item, and item or module events from any list. Event runs carry the triggering
item in the execution context. List workflows name a `list` in every trigger
and require target items for manual runs. A workflow permits manual launching by including a `manual`
trigger; use one manual trigger per workflow. `enabled: false` also disables
manual and webhook launches, except manual launches of built-ins that explicitly
opt into `AllowManualLaunch` (such as document storage optimization).

```json
{
  "name": "Workspace report",
  "scope": "workspace",
  "triggers": [
    { "type": "manual" },
    { "type": "schedule", "cron": "0 8 * * 1" },
    { "type": "webhook" }
  ],
  "variables": { "period": "week", "includeArchived": false },
  "inputSchema": {
    "type": "object",
    "properties": {
      "period": { "type": "string", "enum": ["week", "month"], "default": "week" },
      "includeArchived": { "type": "boolean", "default": false }
    },
    "required": ["period"]
  },
  "steps": [{ "type": "action", "action": "notify", "inputs": { "to": ["admin"], "title": "Report requested", "body": "Period: {var:period}" } }]
}
```

Launch from **Run workflow** on the workspace home page, **Launch** in workflow
settings, or **Run workflow** after selecting list items. The UI uses
react-jsonschema-form and a CSP-compatible validator (JSON Schema draft-07)
to generate and validate
parameter fields. The
workflow editor exposes scope, manual opt-in, and an input schema JSON editor.
Several triggers can be configured through its workflow JSON view.

Manual launches send `POST /v1.0/workspaces/{workspaceId}/workflows/{id}/runs`
with `{ "inputs": { "period": "month" } }`, adding `listId` and `itemIds` for
list workflows. Webhooks send the input object directly to
`POST /v1.0/workspaces/{workspaceId}/workflows/{id}/webhook`. Webhooks use normal
API authentication, the `workflow.write` scope, and workspace Contribute
access. They cannot target items. Neither endpoint starts disabled workflows.

`inputSchema` applies to manual and webhook launches, taking precedence over
the older manual trigger's `inputs` schema. Defaults are applied before server
validation. Supported server constraints are property types, `required`, `enum`,
`minimum`, `maximum`, `minLength`, `maxLength`, nested object properties and array
`items`. Unknown input names are rejected. Scheduled runs have an empty input
object; use run variables or script defaults for values needed by scheduled work.

Every new run stores an `executionContext`, also returned in the run response:

```json
{
  "trigger": "manual",
  "input": { "period": "month", "includeArchived": false },
  "data": {},
  "workspaceId": "…",
  "listId": null,
  "itemId": null,
  "userId": "…",
  "startedAt": "2026-10-04T08:00:00+00:00"
}
```

Variables have separate scopes: `{input:name}` reads the original launch
parameters, `{var:name}` reads mutable run variables, `{trigger:name}` reads
trigger data, `{step:node.path}` reads step outputs, and `{context:workspaceId}`
(or another context path) reads execution metadata. Submitted inputs also
initialize run variables for compatibility. Inputs and variables are local to
the run; workspace scope describes the workflow's target, not shared mutable
workspace variables. Activities receive `WorkflowActivityContext.ExecutionContext`;
scripts receive `context` and `input` alongside `vars`, `steps`, and `trigger`.
Existing persisted runs continue using their original trigger data.

Workspace list-event filters are optional: `list` narrows to a list name,
`contentType` matches a name or template key, `terms` matches term paths and
descendants, and `changedFields` narrows `itemUpdated` to named fields.
The editor exposes these filters; leave the list empty to watch all workspace lists.

Use item-created and item-updated triggers with `parameters.when` to react to
specific field values or changes. The same parameters work across workspace lists
without requiring a named list. See [Parameterized item triggers](#parameterized-item-triggers).

## Approval forms at any step

An approval step can provide its own `inputSchema`, using the same JSON Schema
renderer and domain pickers as manual launches. Each step collects its own answers;
workspace workflows can request approvals without an item.

```json
{
  "type": "approval",
  "name": "Review",
  "assignees": ["admin"],
  "inputSchema": {
    "type": "object",
    "properties": {
      "amount": { "type": "integer", "minimum": 1, "maximum": 100 },
      "route": { "type": "string", "enum": ["standard", "express"] },
      "urgent": { "type": "boolean", "default": false }
    },
    "required": ["amount", "route"]
  }
}
```

`POST /v1.0/me/approvals/{id}/decision` accepts `inputs` alongside `outcome` and
`comment`. The server applies defaults and validates before deciding or resuming
the workflow; invalid input leaves the approval pending. Both approval and rejection
collect the form's required answers. Assignee checks and concurrent-decision handling
remain unchanged.

The approval stores its schema when created, so editing the workflow definition
does not change a pending form. The run keeps its original version for subsequent
steps. The response and approval history include the schema and submitted answers;
the UI opens pending forms for review and displays historical forms read-only.

Later steps read answers through `{step:Review.input.amount}` and
`{step:Review.input.route}`, or `steps.Review.input.amount` in scripts. Approval
outputs also include the outcome, deciding user and comment. The original launch
`input` remains separate from approval answers. Schemas may collect information at
several approval steps in the same workflow.

### Relationship, tag, and keyword input fields

Launch and approval `inputSchema` properties can add an `x-paperdotnet` descriptor
for domain pickers. Values are ordinary IDs in `input` and approval step outputs.
Use `type: "string"` for one selection, or `type: "array"` with string `items` for
multiple selections. Add the property name to its parent object's `required` array
when at least one selection is required. Optional fields can be omitted; optional
multiple fields can be empty. Array `minItems`, `maxItems`, and `uniqueItems` are
validated on the server as well as in the form.

```json
{
  "type": "object",
  "properties": {
    "dependency": {
      "type": "string",
      "title": "Dependency",
      "x-paperdotnet": {
        "kind": "relationship",
        "relationshipType": "Depends on"
      }
    },
    "tags": {
      "type": "array",
      "title": "Tags",
      "items": { "type": "string" },
      "maxItems": 5,
      "uniqueItems": true,
      "x-paperdotnet": {
        "kind": "terms",
        "groupId": "11111111-1111-1111-1111-111111111111",
        "termSetId": "22222222-2222-2222-2222-222222222222",
        "termIds": ["33333333-3333-3333-3333-333333333333"]
      }
    },
    "keyword": {
      "type": "string",
      "title": "Keyword",
      "x-paperdotnet": { "kind": "keywords" }
    }
  },
  "required": ["dependency", "tags"]
}
```

Replace example IDs with existing taxonomy IDs. `kind: "relationship"` requires a
relationship type ID or name and collects readable target items, including items
in other workspaces. It does not create edges when the form is submitted; workflow
steps decide how to use those target IDs and the configured type. Relationship
cardinality constraints still apply when a step creates an edge.

For `terms` and `keywords`, optional `groupId`, `termSetId`, and `termIds` restrictions
are intersected. Explicit `termIds` allow exactly those terms, without descendants.
Without a fixed term set or explicit IDs, the terms picker lets the user choose a
term set, restricted to the configured group if present. Keyword inputs accept only
terms eligible for keyword fields, including promoted keywords. Forms select
existing terms; they do not create terms. Deprecated, missing, inaccessible item,
out-of-scope, duplicate, or malformed selections are rejected before starting a run
or deciding an approval. Each domain field accepts at most 100 IDs. Nested objects
and repeated object sections use the same pickers.

### Parameterized item triggers

`itemAdded` and `itemUpdated` accept `parameters: { "when": condition }`.
Each condition is a leaf, an `all` group (AND), or an `any` group (OR).
Groups cannot be empty. Multiple triggers are alternatives and still create at
most one run per workflow and event.

```json
{
  "scope": "workspace",
  "trigger": {
    "type": "itemUpdated",
    "parameters": {
      "when": {
        "all": [
          { "target": "tags", "operator": "added", "term": "Documents/Tags/Receipt" },
          { "target": "field", "field": "status", "operator": "eq", "value": "Ready" }
        ]
      }
    }
  }
}
```

| Target | Operators | Operands |
| --- | --- | --- |
| `field` | `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `startsWith`, `endsWith` | `field` and `value` |
| `field` | `isEmpty`, `isNotEmpty`, `changed` | `field`, without `value` |
| Collection field | `containsAny`, `containsAll` | `field` and a nonempty `value` array |
| Collection field | `added`, `removed` | `field`; optional single `value`, otherwise any added/removed element |
| `field` | `transition` | `field`, `from`, and `to` |
| `tags` | `contains` | Required `term` path |
| `tags` | `added`, `removed` | Optional `term` path; omit for any term |
| `tags` | `isEmpty`, `isNotEmpty` | No operand |

Fields use their configured names, including `title`. Numbers and currencies compare
numerically, dates chronologically, text ordinally and case-sensitively, and reference
fields by ID. Collection equality and changes use set semantics; reordering alone
is not a change. Ordering requires a scalar number, date or text field; text
operators require text fields; collection operators require multiple-value fields.
Operands must have the field's type, without string/number coercion.

`tags` aggregates only managed-metadata and keyword fields, including promoted
keywords. A term path matches descendants by default; set `includeDescendants: false`
for an exact term. Use groups to combine several term paths. An item without any
configured taxonomy fields does not match a tag condition, even `isEmpty`.

Value checks use the after snapshot. On creation, assigned collection values count
as additions; `changed`, `removed`, and `transition` are update-only. A field absent
from the content-type schema, or incompatible with the condition, does not match,
including negative comparisons. A declared optional field without a value is empty.
Empty means null, an empty string, or an empty array; whitespace is not empty.

Save validation rejects malformed trees, unsupported operators, invalid term paths,
and incompatible known list fields. Trees are limited to eight levels and 100 total
nodes; comparison arrays contain at most 100 primitive values. Workspace conditions
are validated structurally and checked against each event's field types. Explicit
list conditions must reference a field on an eligible content type in that list.

The editor provides a **Trigger parameters** JSON field; `{}` removes parameters.
The JSON view also supports parameters on every entry in `triggers`. Existing
`changedFields`, `terms`, and workflow-level OData conditions remain additional AND
guards. Parameters are supported only on item-created/item-updated triggers.

Item event execution context includes `context.data.before`, `context.data.after`
(each with identity, `fields` and `fieldTypes`) and `context.data.changedFields`.
Creation has a null before snapshot; deletion has a null after snapshot. New
conditions never fall back to live item values: old queued events without complete
snapshots skip parameterized matches and produce a warning diagnostic. Existing
legacy workflows continue using their original filters.

The former `tagAdded` trigger is removed. SQLite and PostgreSQL data migrations
disable every existing definition containing it, including definitions with several
triggers, without changing historical versions or runs. Replace it with `itemAdded`
and/or `itemUpdated` plus an added-tag condition, then explicitly re-enable the
workflow. Invalid trigger definitions cannot be saved.

### Trigger conditions and workflow-level OData

`parameters.when` and the workflow-level `condition` overlap, but neither is a
complete replacement for the other. The existing OData condition remains supported
and is not deprecated. References to it as "legacy" mean the previously existing
mechanism, not a requirement to migrate or remove it.

| Capability | Trigger `parameters.when` | Workflow-level OData `condition` |
| --- | --- | --- |
| Field comparisons, text matching, collection membership, AND/OR | Supported | Supported through the items API's filter syntax |
| Added/removed tags or collection values, changed fields, before-to-after transitions | Supported | No access to the previous values |
| Text transformations such as `tolower` and `toupper` | Not supported | Supported |
| Item metadata such as `createdAt`, `updatedAt`, and `createdBy` | Not currently exposed as condition fields | Supported |
| Trigger sources | `itemAdded` and `itemUpdated` only | Can guard other triggers with an associated item |
| Values inspected | Snapshots captured with the source event | Current item when processing the event |
| Evaluation | In memory; taxonomy resolution cached per event | An item query for each workflow whose triggers match |

For creation/update workflows whose checks fit the supported operators, use
`parameters.when` alone; an OData condition is not required. Retain OData when its
additional capabilities or a check of the current item are needed. Existing OData
workflows continue to work without adding trigger parameters. Workflows without an
associated item do not evaluate the item-level OData guard.

When both are configured, the order is:

1. Check the event type, list/content-type selectors, and other trigger filters,
   including `parameters.when`.
2. If any trigger matches, evaluate the workflow-level OData condition once.
3. Start one run only if both checks pass. An OData evaluation error produces a
   failed run so the error remains visible.

For example, an update sets `status` to `Ready`, followed immediately by another
update setting it to `Draft`. When the first event is processed later,
`parameters.when` checking `status eq Ready` matches its captured after snapshot.
An additional OData `condition` of `fields/status eq 'Ready'` can reject that same
event because the current item is already `Draft`. Keeping OData's live-item
evaluation preserves the behavior of existing workflows.

Do not assume mechanically translated filters have identical behavior: new
conditions explicitly use ordinal, case-sensitive text comparisons, collection
set semantics, and non-matches for missing or incompatible fields. Check these
semantics when replacing an existing OData condition.

## Manual workflows over a selection

A manual trigger can specify `selectionMode: "selection"` to start **one run**
for all selected items. Omitting it, or specifying `"perItem"`, retains one run
per item. Selection execution requires 1–100 distinct items from the same list;
workspace launches without items and other trigger types cannot use it.

The existing workflow and library built-in launch endpoints accept ordered
`itemIds` and an optional `primaryItemId` belonging to that selection. The first
item is primary when omitted. Duplicates are removed while preserving order.
Every target and the input form are validated before a run starts. The response
remains an array, containing one run for selection execution.

`context.items` contains ordered `{ workspaceId, listId, itemId }` references,
and activities receive these as `WorkflowActivityContext.Items`. The singular
item context remains the primary item for existing actions and approvals.
Membership survives retries and recovery. `skip` and `replace` concurrency
policies compare every member, including selections with different primary
items. Run queries filtered by an item include runs where it is any member.
Deleting or purging a member cancels its active selection runs; a workflow's own
reviewed recycling excludes that originating run. A cross-library move does not
rewrite the selection's original locations.

The workflow editor offers **Once for the entire selection** on item-based
manual triggers. Built-in catalogs expose `manualSelectionMode` so the launch
form can display the correct behavior before a workflow row exists.
