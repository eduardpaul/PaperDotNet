# ADR-0023: Item mutators before the save, events for everything after it

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Item event receivers (`IItemEventReceiver`, idea 0012) had two halves, as in
SharePoint:
- **Before** hooks (`ItemAdding`, `ItemUpdating`, `ItemDeleting`) ran inside the
  write and could change the values or cancel the write.
- **After** hooks (`ItemAdded`, …) ran in the request after the commit.

The after hooks duplicated integration events (`IEventSubscriber<ItemAdded>`)
and automation rules, with weaker guarantees:
- their work was lost on a crash, a client disconnect or a rolling deploy;
- their errors were only logged;
- they made the request slower;
- on several servers they ran wherever the request landed, with no retry.

Nothing in the product used them; only a test did.

## Decision

- **Item mutators.** `IItemMutator` (Lists.Contracts) keeps only the before half:
  `ItemAddingAsync`, `ItemUpdatingAsync` and `ItemDeletingAsync` with an
  `ItemMutationContext`. A mutator can change `After` (the values are
  validated again) or `Cancel` the write (HTTP 409 `cancelledByMutator`).
  Mutators are stateless and run in the request on any server; `Sequence` and
  `AppliesTo` order and scope them as before.
- **No after hooks.** Every reaction to a saved change is an integration event
  handled in the background: `IEventSubscriber<T>` for code, automations for
  configuration. They are durable (transactional outbox), retried and
  dead-lettered.
- **Extensions:** `AddItemReceiver`/`ItemReceiverOptions` become
  `AddItemMutator`/`ItemMutatorOptions`; the extension listing reports
  `itemMutators`.

## Consequences

- There is a single, reliable model for "after": events. Code that must see
  the change immediately in the same request should be a mutator; everything
  else accepts eventual consistency.
- Breaking change for extensions and API clients (error code, contribution
  name); acceptable before the first release.
