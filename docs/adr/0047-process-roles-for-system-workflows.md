# ADR-0047: Process roles: system workflows developers can replace

- **Status:** Accepted (implemented)
- **Date:** 2026-10-06
- **Builds on:** [ADR-0036](0036-workflows-as-the-core.md), [ADR-0038](0038-documents-composed-from-workflows.md),
  [ADR-0043](0043-search-indexing-as-workflows-over-a-search-store.md)

## Context

Product processes now run as system workflows: search indexing, the activity timeline, follower alerts, note links, task
reactions, change notifications and search maintenance. The aim of PaperDotNet is that any developer can adapt them. For
example, a DMS that also runs LLM or OCR steps should be able to put those steps into its own search indexing, and still
keep search's guarantees.

The first version tied everything to one built-in workflow per process:
- Copying "Index for search" made an ordinary workflow. The copy lost the system treatment (depth limit, own queue, short
  history).
- Search status, the repair sweep and the chunk settings all looked for the built-in key, so they broke once the copy
  replaced it.
- `Required` locked processes completely, so even follower notifications could not be changed.

We need flexibility for *how* a process works, and a guarantee for *what* it delivers.

## Decision

**A process is a role, not a workflow.**

- Each system process has a role key. The role's default is the built-in whose key is the role (`search.index`,
  `notifications.alertFollowers`, …). The default's flags are the role's: system, required, locked and folders.
- A workflow fills a role through `provides` in its definition (stored as `role` on the row). Built-ins provide their role.
  Copies keep it. Extensions can ship alternatives with `BuiltInWorkflow.Role` (for example search's "Index for search
  with AI context").
- The engine gives every workflow that fills a system role the system treatment: depth limit lifted, folders where the
  role asks for them, its own queue (`ResumeSystemRun`), short retention.
- **One active workflow per role** in its scope (the workspace, or the list for list roles). Turning one on turns the
  others off: enabling an alternative built-in, copying a built-in (`…/builtIns/{key}/copy`, per list under
  `lists/{listId}/workflows/builtIns/{key}/copy`), saving or applying a workflow with `provides`.
- **Required roles are replaceable but never empty.** The default can't be turned off while nothing else fills the role.
  Turning off or deleting the last replacement turns the default back on.
- **Locked roles are the guarantees:** removing deleted items from search, moving permission scopes, maintaining
  excluded or deleted lists, and queueing API change notifications. They cannot be replaced, copied, turned off or
  deleted, and they run even where their row was turned off.
- Consumers ask for the role, not a built-in. `IWorkflowDirectory` has `IsRoleActiveAsync`, `GetRoleWorkflowAsync`
  (the active workflow and its version) and `GetLatestRunAsync(role)`. `POST lists/{listId}/workflows/roles/{role}/runs`
  runs the role's active workflow, or its built-in when automatic runs are off.
- Explicit requests (`AllowDisabledBuiltIns`) start a disabled built-in only when no other workflow fills its role there.

**The search indexing contract.** Any workflow that fills `search.index`:
1. stages a generation, with `search.chunk` (a chunker) or `search.stage` (chunks given by an earlier step: a script, an
   OCR or AI step);
2. may change the staged chunks, for example with `search.enrich` (chat-model context, cached by instructions, title and
   chunk text);
3. publishes with `search.publish`, which checks the revision, the list policy and the generation claim;
4. may embed with `search.embed`.

Steps with nothing to do end at their `skipped` port. Because publication and the query-time filters are fixed code, a
custom pipeline can change quality and cost, but never permissions or inclusion.

A publication records the pipeline that made it (workflow id and version). Switching the role's workflow, changing it or
its parameters marks publications stale, and the repair sweep indexes them again.

## Consequences

- Developers adapt processes in the product's own terms: copy and edit a workflow, or ship one in an extension. Search's
  status, rebuilds, "Index now" and the UI follow whichever workflow fills the role.
- List settings have an **Indexing pipeline** section: the built-in, its alternatives and the list's own copies, with
  **Use** and **Customize**.
- Workflow responses carry `provides`, `system`, `required` and `locked`. Built-in responses carry `role`, `required` and
  `locked`. Templates export a list's own copy with its `List`, and never the locked defaults.
- A new release that changes a built-in's definition gives it a new version, which marks its publications stale once.
- Chunks know whether they come from the document's own body (`PassageText.FromBody`). Stores add the other chunks (pages,
  chunks a pipeline made or enriched) to the searchable body.
- Open: a role per tenant (one pipeline for all lists) instead of per list, and enrichment in batches (`ai.batch`) for
  large libraries.
