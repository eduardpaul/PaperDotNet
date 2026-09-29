# ADR-0036: Workflows as the core of automation (built-in and user-defined)

- **Status:** Proposed (extends [ADR-0024](0024-one-automation-model.md) and [ADR-0019](0019-workflows-on-wolverine.md); keeps [ADR-0025](0025-reliable-runs-on-several-servers.md))
- **Date:** 2026-09-29

## Context

Workflows should become the main way to shape how a workspace behaves. They
should react to timers, to events (item added, updated, deleted, and more) and
call LLMs ([idea 0022](../../ideas/0022-llm-extraction-workflow-step.md),
AI-07). One model should cover:

- **built-in workflows**, which the product and extensions ship;
- **user-defined workflows**, which owners create.

[Idea 0009](../../ideas/0009-automation-rules-engine-elsa.md) proposed Elsa for this.

### What we have today

Automation (ADR-0024) already provides one model with:
- a trigger, an OData condition and steps (`action`, `approval`, `delay`,
  `condition`);
- versions;
- durable runs through outbox messages, with leases and recovery;
- tenant-owned tables with row-level security (RLS);
- portable templates, extension actions and triggers (EVT-09), and a web editor (8h).

This foundation is solid. The gaps are in the model and in coverage:

| Area | Gap |
|---|---|
| Triggers | Only `manual`, item events and extension triggers. There is no schedule (cron), and nothing fires on a date relative to a field ("3 days before `dueDate`"). No trigger fires when a document has been processed: an `itemAdded` automation runs before the OCR text exists, so rules based on the text are impossible. There is also no trigger for terms added, approvals decided, tasks completed or inbound webhooks. |
| Steps | Steps form a sequence with nested if/else only. Steps cannot pass data to each other: `{outcome:Step}` is the only output. There are no loops (for each), no parallel branches, no wait for an event, no sub-workflows, no error paths, and no HTTP or LLM steps. |
| Scope | Automations exist only per workspace. There are no organization-wide workflows, and a workflow cannot be attached to a content type the way idea 0022 wants. |
| Built-in processes | Product behavior is hard-coded in subscribers and jobs: the document pipeline (`DocumentProcessor`), reminders (`DueTaskReminderJob`, `EventReminderJob`), recurring tasks (`RecurringTaskSpawner`), alerts and digests. Users cannot see, disable or extend any of it, for example by adding "classify with AI" after OCR. |
| Authoring | The only editor is a form for a list of steps. Nothing can test a workflow on an item or show the data flow of a run. |
| Identity | Every run acts for the organization. That is fine inside a workspace, where workspace managers have Manage access. It is not safe for organization-wide workflows or anything that crosses workspaces. |

### Elsa, re-checked on 2026-09-29

Elsa's concepts fit this well:
- activities with typed inputs and outputs, and flowcharts with outcome ports;
- bookmarks: a durable wait on a key;
- triggers indexed by workflow;
- variables and expressions;
- workflows defined in code or JSON;
- incidents and alterations.

Elsa as a **package** still does not fit:

- **License.** Elsa 3.8.4, the latest version, depends on JsonSchema.Net 9.4.0 even in `Elsa.Workflows.Core` alone. JsonSchema.Net pulls in JsonPointer.Net and Json.More.Net. All three ship under the json-everything *Open Source Maintenance Fee* EULA, which requires license acceptance and charges organizations above US$10k revenue. Every business that self-hosts PaperDotNet would inherit that. This conflicts with our policy and with the Apache-2.0 promise (ADR-0005).
- **Elsa 3.7.1** is the last clean version: its core is 16 packages and targets `net10.0`. Using it has three drawbacks:
  - It would be frozen, with no fixes.
  - Its core still pulls pre-release packages: CShells 0.0.x, a Newtonsoft beta, a ShortGuid pre-release and a Snappier beta.
  - The full runtime adds about 50 packages. These include FastEndpoints betas, a second identity layer and a second tenant layer, plus its own tables outside our migrations, tenant filters and RLS (ADR-0013).
- **Designer.** Elsa Studio is Blazor. The web UI is React on our SDK (ADR-0033), so we would build our own designer anyway.
- **Runtime.** Our runtime (outbox, leases, recovery, RLS) is already the hard part, and it works on SQLite and PostgreSQL. Elsa would bring a second runtime next to Wolverine.

## Decision (proposed)

**Use Elsa as the design reference, not as a dependency.**
- Evolve our engine into an Elsa-shaped model: activities, flowcharts, bookmarks, variables and trigger providers.
- Keep that model behind one module. If Elsa ever drops the fee-licensed dependency, swapping the engine is possible because definitions stay our own JSON.
- Rename *automation* to **workflow** in the API and UI. We are pre-release, so existing automations are converted by a migration.

### 1. Definition

A workflow has:
- **triggers** (one or more);
- an optional **condition**;
- **variables** and **inputs**, typed and filled by the trigger or a manual start form;
- a **body**, in one of two authoring forms stored in the same model:
  - **`steps`**: the current sequence with if/else, kept as the simple form;
  - **`flow`**: nodes and connections by outcome port (`done`, `approved`, `rejected`, `true`, `false`, `error`, …) for branching, merges, loops and parallel paths. A sequence is a flow without branches, and the engine runs only flows.

```json
{
  "name": "Receipts: extract and file",
  "triggers": [{ "type": "document.processed", "list": "Receipts", "terms": ["Tags/Receipt"] }],
  "flow": {
    "start": "extract",
    "nodes": {
      "extract": { "activity": "ai.extract", "inputs": { "fields": ["store", "purchaseDate", "total", "products"], "mode": "apply", "minConfidence": 0.8 },
                   "next": { "done": "file", "lowConfidence": "review", "error": "review" } },
      "file":    { "activity": "item.file", "inputs": { "folder": "{purchaseDate:yyyy}/{store}" } },
      "review":  { "activity": "task.create", "inputs": { "list": "Tasks", "title": "Check {title}: {step:extract.reason}", "assignedTo": ["creator"] } }
    }
  }
}
```

Definitions are versioned: draft, then published. A run keeps its version, as it does today.

### 2. Triggers

Triggers are pluggable through `IWorkflowTriggerProvider` in Workflows.Contracts. Each trigger declares its payload as JSON Schema, generated with `System.Text.Json.Schema.JsonSchemaExporter` (no JsonSchema.Net).

| Trigger | Source |
|---|---|
| `item.added/updated/deleted/restored` (list, content type, changed fields, terms added or removed) | Lists events (existing) |
| `document.processed` (text and OCR ready), `document.versionAdded` | Documents, raised after `DocumentProcessor` succeeds |
| `schedule` (cron in the workspace time zone, stored as UTC) | The scheduler (ADR-0010); one run per occurrence, with the occurrence time as its key |
| `date` (N minutes, hours or days before or after a date field of items matching a filter) | A minute job over indexed date fields (ADR-0035); one run per item and date value |
| `approval.decided`, `task.completed`, `comment.added` | Existing events of Automation, Tasks and Collaboration |
| `webhook` (inbound POST with a per-workflow secret, rate-limited) | New endpoint |
| `manual`: on an item, on a selection (fan-out, bounded) or without an item, with an input form | Existing, extended |
| Extension triggers | EVT-09 (existing `IAutomationTriggers`, renamed) |

Trigger matching stays in the event subscriber: one run per workflow and event, with the unique index from ADR-0024. Schedule and date triggers use the occurrence as the event key, so a redelivery or a second server starts nothing.

### 3. Activities

`IWorkflowActivity` is a superset of `IAutomationAction`.
- It declares its inputs, outputs and outcome ports as schemas, which the UI turns into forms.
- It returns `Done(outcome, output)`, `Fault(error)` or `Suspend(bookmark)`.
- The `ExecutionKey` and `ExecutionId` rules for safe repeats stay (ADR-0025).

| Group | Activities |
|---|---|
| Flow | if/switch, for each (items from a query or an array; bounded, at most N per run), parallel and join, delay and delay-until, **wait for event** (a bookmark on an item change, a signal or a webhook), call workflow, set variable, end, fail |
| Items | create, update, move, file, delete, query, add or remove terms, check in or out |
| Documents | process again (OCR), split or extract pages |
| People | approval (existing), task create, notify, **ask for input** (a form assigned to a person, a bookmark) |
| Integration | HTTP request (per-tenant allow-list of hosts, SSRF protection, secrets from the tenant's secret store), webhook send, email once NTF-04 exists |
| AI (`PaperDotNet.AI`, off by default) | `ai.classify` (content type and terms from a term set, AI-02), `ai.extract` (structured output into chosen fields, AI-03 and AI-07), `ai.summarize` (AI-04), `ai.prompt` (a prompt returning text or JSON as a variable). Each uses the tenant's provider (AI-01). A **suggest** mode writes suggestions and an **apply** mode writes values above a confidence. Results are cached by content hash and prompt, and every call counts against quotas and is audited (AI-06). |
| Extensions | `AddWorkflowActivity<T>()` (was `AddAutomationAction`), gated per tenant |

### 4. Data and expressions

- Tokens stay and gain the prefixes `{trigger:x}`, `{var:x}` and `{step:Name.x}` (a step's output), next to item fields and the existing `{data:x}` and `{outcome:Step}`.
- Conditions are OData on the item (existing), or structured comparisons (`left`, `op`, `right`) on variables and outputs.
- **No general-purpose script language** (JavaScript or C#) in user workflows: it brings sandboxing, resource and security costs for little gain. Code belongs in extension activities. We can revisit this if structured comparisons prove too weak.

### 5. Engine

The same module and messages, generalized from a flat program to a graph:

- **Run state:** active tokens (node, iteration and branch) plus variables and step outputs (JSON, with a size limit per run).
- **Bookmarks:** a table (`run`, `node`, `kind`, `key`, `resumeAt`). Approvals, delays, waits for events and input requests all become bookmarks. `ResumeRun(runId, bookmarkId)` replaces `waitKey`, and the minute job resumes due bookmarks as it does now.
- **Kept from ADR-0019 and ADR-0025:**
  - the start and the first message in one transaction;
  - leases, recovery of stuck runs, the attempts limit and retention;
  - the loop depth through `EventCausation`.
- **Errors:** each activity has a retry policy (count, backoff). After the retries, the `error` port runs if it is connected; otherwise the run fails with an **incident**. An incident can be retried from the failed node after a fix.
- **Limits:** maximum nodes executed per run, items per for-each, parallel branches, run duration, and runs per tenant per day. Limits are configurable, with PLT-06 quotas later.

### 6. Built-in workflows

Modules and extensions ship workflow definitions in code through `IWorkflowDefinitionProvider`, as JSON or a small C# builder. These definitions:
- are versioned with the release and read-only;
- appear in the UI next to user workflows, marked *built-in*;
- can be enabled or disabled per organization, workspace or list;
- can be **customized by copying**: the copy becomes a user workflow, and the built-in one is disabled for that scope.

Only processes that people need to see or vary move into workflows. Infrastructure (search indexing, activity recording, cleanup jobs, the OCR engine itself) stays code and publishes triggers.

| Today | Becomes |
|---|---|
| OCR, thumbnails and text in `DocumentProcessor` | Stays code; publishes `document.processed` |
| (planned AI-02/03) classify and extract on upload | Built-in workflows "Classify new documents" and "Extract fields". Enabled when AI is configured; the `ai.*` activities do the work. |
| Task and event reminders, recurring tasks | Stay code for now (per-user preferences, RRULE). Custom reminders are `date` trigger workflows. |
| Alerts and digests (NTF-03) | Stay code (per-user subscriptions) |
| Document approval patterns | Built-in templates (not enabled): "Approve invoices", "Review contracts" |

### 7. Scopes and identity

- **Workspace workflows** (workspace managers) act only inside their workspace. Every action checks that its targets belong to the run's workspace.
- **Organization workflows** (`organization.manage`) can target any workspace and appear in each workspace's list as read-only.
- **Associations:** a workflow can be attached to a list or content type from the list settings ("workflows for this content type"). This covers idea 0022 and SharePoint's list workflows, and is stored as trigger filters.
- **Run identity:** runs keep acting for the organization, but they are confined by scope as above. The log records the triggering user and the workflow author. An optional "run as the triggering user" mode checks item permissions (ADR-0035) for manual starts.

### 8. UI, API and portability

- **API:** `/workspaces/{ws}/workflows`, `/organization/workflows`, `…/workflows/runs`, `…/runs/{id}/retry` and `/v1.0/workflows/catalog` (triggers and activities with their schemas). There is also a test run: a dry run on a chosen item that runs no side effects and records what would happen. `/me/approvals` grows into an inbox of approvals and input requests.
- **UI:**
  - The simple editor, which is today's step form, generated from activity schemas.
  - A **flow designer** built on React Flow (`@xyflow/react`, MIT).
  - A run inspector: the path taken, each node's inputs and outputs (secrets redacted), incidents and retry.
- **Templates:** `urn:paperdotnet:workflow:1`, with references by name as today. Built-in workflows are referenced by key.

## Plan

Slices, each shippable and tested on both databases:

| Slice | Content | Features |
|---|---|---|
| **9a Engine** | Rename to workflows; graph model (`steps` compiled into a flow); run tokens, variables and step outputs; bookmarks table; activity descriptors with schemas; error ports, retries and incidents; migration of existing automations | EVT-07, EVT-08 |
| **9b Triggers** | `IWorkflowTriggerProvider`; `schedule`; `date`; `document.processed`; term changes; `approval.decided`, `task.completed`, `comment.added`; manual with an input form and selection | EVT-10, EVT-11 (new) |
| **9c AI activities** | AI-01 per-tenant provider; `ai.classify`, `ai.extract`, `ai.summarize`, `ai.prompt`; suggestion mode; AI-06 cache, quotas and audit | AI-01…04, AI-06, AI-07 |
| **9d Built-in workflows** | `IWorkflowDefinitionProvider`, catalog, enable, disable and copy; "Classify new documents", "Extract fields" and approval templates | EVT-12 (new) |
| **9e Flow control and integration** | For each, parallel and join, wait for event, call workflow, ask for input; HTTP request, outbound and inbound webhooks with allow-lists and secrets | EVT-08, EVT-13 (new) |
| **9f Designer** | Flow designer, run inspector, test runs, association from list settings | EVT-14 (new) |
| **9g Scopes** | Organization workflows, confinement checks, run-as-user for manual starts, per-tenant limits | EVT-15 (new), PLT-06 |

After 9a and 9b, the first three goals are covered: timers, events and LLM calls (9c).

## Consequences

- One engine for every process users can see, with built-in workflows as its first users. This keeps the engine honest.
- No new dependency except `@xyflow/react` in `web/` (MIT). The license policy stays intact, and all workflow data stays tenant-owned under RLS.
- We keep maintaining our own engine. Parallel branches and joins are the risky part (9e) and come after the linear features pay off.
- The rename touches the API, SDKs, UI, templates and docs (`automation.md`) once, before release.
- Elsa stays an option: re-check its dependencies when a new version ships. A swap would replace `PaperDotNet.Workflows` internals, not definitions or the API.

## Open questions

1. Accept the rename to *workflows*, or keep *automations* in the API?
2. Elsa: stay with this reference-only decision, or accept the maintenance fee terms (a change to the license policy) or a frozen 3.7.1?
3. Should organization workflows be in 9g, or earlier for organization-wide retention and AI classification?
4. Is "run as the triggering user" needed beyond manual starts?
