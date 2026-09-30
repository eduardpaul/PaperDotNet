# ADR-0037: Scripts in workflows, and fixes to the flow language

- **Status:** Accepted (extends [ADR-0036](0036-workflows-as-the-core.md))
- **Date:** 2026-09-30

## Context

The flow language of ADR-0036 is JSON: nodes with an activity, inputs with `{tokens}`, and ports. It is good at
orchestration: triggers, waits, approvals, retries and error paths, all durable. It is poor at working with data.
The receipts sample showed the limits (it needed `forEach`, `item.create` and `item.delete` to write eight lines):

- **One item.** `item.update` can only change the run's item. Bare tokens (`{title}`) only read that item, and
  field names share a namespace with reserved words (`{id}`, `{list}`, `{today}`).
- **No expressions.** Values are text templates. Types survive only in the special case "one token into a field
  that is not text". There is no arithmetic, no defaults and no reshaping of arrays.
- **One node per change.** Writing N items takes about 2N+ nodes. Each one is a save of the run, with events and a
  failure point. A run failed after 500 nodes in one execution instead of continuing.
- **Loops.** A loop inside a loop that a pass left early kept its position, so the outer loop's next element
  continued it instead of starting it again. Nested loops could use the same variable.
- **Concurrency.** Two runs of a workflow on the same item run side by side (the receipts CI failure).

We considered three ways to express logic over data:

| Option | For | Against |
|---|---|---|
| Grow the JSON language (expressions such as JSONata, bulk-write actions, more flow activities) | Stays declarative and visual | Becomes a programming language written in JSON: verbose, hard to read, and every new need becomes a new activity |
| C# scripts (Roslyn scripting) | Familiar to the team, full language | No sandbox in .NET: a script runs with the server's rights (reflection past the tenant filters, files, secrets). Compiled scripts cannot be unloaded. A script cannot pause mid-way for a wait. Templates would carry C# tied to our internal APIs. Right for the operator, wrong for workflows that tenants write |
| A sandboxed script step (JavaScript on [Jint](https://github.com/sebastienros/jint)) inside the flow | Real language for data work; hard limits on time, memory and statements; no access to .NET; only the objects we pass in. Used the same way by OrchardCore and Elsa | A second way to express logic; JavaScript instead of C# |

Trusted C# already has a place: extensions, compiled into the host (ADR-0014).

## Decision

**The graph orchestrates; a script step does the data work.**

### The `script` step

A flow activity `script` runs JavaScript (ECMAScript 2023, strict mode) with Jint:

- **Code:** `code`, a string or an array of lines. It is checked for syntax when the workflow is saved. It runs as
  the body of an async function: `return` gives the node's `result`, and `items` calls are awaited, as with the SDK.
- **What it sees** (JSON copies, no .NET objects):
  - `item`: the run's item (`id`, `list` and its fields), or null;
  - `vars`: the run's variables (changes are kept);
  - `steps`: the outputs of earlier nodes;
  - `trigger`: the trigger's data;
  - `items`: the lists of the workspace, by name: `get(list, id)`, `query(list, { filter, orderBy, top })`,
    `create(list, fields)`, `update(list, id, fields)` and `delete(list, id)`, each returning a promise;
  - `log(text)`: a line in the run's log.
- **Writes are planned, then applied.**
  - Reads go to the database right away. `create`, `update` and `delete` only add to a plan, and `create` returns
    the new item's id (derived from the step's execution id), so later writes can refer to it.
  - When the script has returned, the plan is saved with the run, then applied in order through the normal write
    path (validation, mutators, events, search).
  - Every write is safe to repeat. A crash, a retry policy or a retried run continues the saved plan and does not
    run the script again, so a script is never run twice for one execution.
  - Not all-or-nothing: Lists saves each item on its own. A failing write stops the step there (retry continues from
    it). A write batch in Lists can come later without changing scripts.
  - A failed `items` call fails the step even when the script does not await it or catches it, because the plan would
    be incomplete. A failed script writes nothing.
- **Limits** (server settings `Workflows:Scripts`): 2 seconds, 32 MB, 1,000,000 statements, recursion depth 100,
  200 reads and 1000 writes per run of the step, and 50,000 characters of code. JavaScript has no access to .NET,
  files, the network or timers.
- **Ports:** `done` and `error`. Output: `result`, and `created`, `updated` and `deleted`.

### The contract lives in the SDK

Script authors should learn one API, with types, and be able to run a script outside a workflow. Running the generated
SDK inside the sandbox is not the way:
- it is megabytes of code for every run;
- it needs `fetch`, timers and a token;
- it writes at once, which loses the plan.

A full JavaScript engine (V8) would be a native dependency on every platform. So:

- `@paperdotnet/client` defines the contract with the SDK's types: `ScriptGlobals`, `ScriptItems`, `ScriptWrite`,
  `scriptLimits`, and `scriptDeclarations` (the globals as TypeScript declarations, for code editors).
- `runWorkflowScript(client, options)` runs the same contract in Node on the real client: reads through the API, writes
  planned and then applied by `applyScriptPlan`. It runs with the caller's rights and no sandbox; it is for writing and
  testing scripts (`apply: false` is a dry run). Workflows run their scripts on the server.
- Creates in a plan carry their id. The items API takes an optional `id` on create for this: repeating the create returns
  the item it made (200), and an id used by another item is a conflict (409). Applying a plan again continues it.
- The server keeps its Jint implementation of the contract. Shared contract tests (`sdk/typescript/test/scripts.test.mjs`)
  run every case with both and expect the same result, variables, log, errors and writes.

### Fixes to the flow language

- **Explicit items.** `item.update` takes an optional `list` and `id` to change another item. New actions:
  `item.get` (`list`, `id`: the item's fields as output) and `items.query` (`list`, `filter`, `top`: the items).
- **An unambiguous token.** `{item:name}` is always the run item's field, whatever the field is called. The bare
  `{name}` keeps working.
- **Long runs pause instead of failing.** After 500 nodes in one execution, the run saves and continues in a new
  message. A run executes at most 10,000 nodes.
- **Loops.**
  - A loop inside a loop starts again for each element of the outer one, even if an earlier pass left it early.
  - Nested loops cannot use the same `as` variable.
- **Concurrency per item.**
  - A definition may say `"concurrency": "skip"` (a new run on an item with a run still going does not start) or
    `"replace"` (the running one is cancelled). The default, `"parallel"`, is the current behavior.
  - It is checked when a run is started. It is checked again when the run begins executing, so two runs started at
    the same moment (two quick changes) still end up as one: with `skip` the earlier run is kept, with `replace` the
    later one.

- **Several triggers per workflow** (added after the receipts package needed the same flow twice):
  - `triggers` lists up to 10 triggers; any of them starts a run, and one event starts one run. `trigger` keeps
    working.
  - The workflow's trigger column holds the types joined with commas, so workflows for an event are still found in
    the database, without a new table.
  - Schedule and date triggers keep a state each; a manual start uses the `manual` trigger.
- **Trigger data filters.** A module or extension trigger can require values of its data (`"data": { "hasText": false }`),
  so a workflow does not start runs only to find there is nothing to do.

### Not now

- **Collecting loop results in `forEach`.** Scripts do this more simply.

## Consequences

- The receipts package reads a receipt with `ai.prompt` and saves it with one `script` node of about 10 lines, with
  `"concurrency": "replace"`.
- The script API changes only together with the SDK, and the contract tests keep both implementations the same.
- Jint (BSD-2-Clause) and its parser Acornima (BSD-3-Clause) are new dependencies, allowed with notice.
- Scripts are data in the definition: they are versioned with the workflow, travel in templates and are checked in
  dry runs like the rest of the flow.
- The web editor shows scripts in the JSON view for now. The designer (9f) gets a code editor.
