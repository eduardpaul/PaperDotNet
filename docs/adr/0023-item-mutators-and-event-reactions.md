# ADR-0023: Item mutators before the save, events for everything after it; one message per subscriber

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
- **One message per subscriber.** Before, one Wolverine message carried an
  event to all its subscribers in a loop: one failing subscriber made the
  others run again on every retry, and in the end dead-lettered the event for
  all of them (search, notifications, automation). Now the outbox publishes one
  `EventEnvelope` per subscriber (`Subscriber` = its registered name) in the
  same transaction as the change. Subscribers are registered with
  `AddEventSubscriber<TEvent, TSubscriber>()` (PaperDotNet.Abstractions) as
  keyed services named after their type; a message for a subscriber that no
  longer exists is dropped with a warning.
- **Extensions:** `AddItemReceiver`/`ItemReceiverOptions` become
  `AddItemMutator`/`ItemMutatorOptions`; the extension listing reports
  `itemMutators`.

## Consequences

- There is a single, reliable model for "after": events. Code that must see
  the change immediately in the same request should be a mutator; everything
  else accepts eventual consistency.
- Each subscriber is retried, dead-lettered and replayed on its own. An event
  with n subscribers is stored as n messages (small JSON payloads).
- Renaming a subscriber class changes its name: messages queued under the old
  name are dropped. Keep names stable, or drain the queues before deploying.
- Breaking change for extensions and API clients (error code, contribution
  name); acceptable before the first release.
