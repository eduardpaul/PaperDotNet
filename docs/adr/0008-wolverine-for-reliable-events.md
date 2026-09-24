# ADR-0008: Wolverine for reliable events (outbox)

**Status:** Accepted (2026-09-24); implemented in phase 1c.

## Context
After-event handlers (idea 0012), search indexing, webhooks, notifications
and automation need at-least-once delivery tied to the database transaction.
The options were our own outbox (+ Channels) or Wolverine; the user chose
Wolverine.

## Decision
- **Wolverine** (MIT) with the **EF Core transactional outbox** and the message
  storage of the configured database (ADR-0009): `PersistMessagesWithSqlite`
  (durability mode `Solo`, single node) or `PersistMessagesWithPostgresql`
  (schema `wolverine`). Messages are stored in the same database and the same
  transaction as the data; no broker, still one container.
- Wolverine keeps its **own tables** (`wolverine_*`); they are not mapped into
  module DbContexts or EF migrations. The outbox enrolls the module DbContext
  and writes envelopes inside its transaction.
- **Encapsulation:** only `PaperDotNet.Messaging` (and the host) reference
  Wolverine (architecture test). Modules use transport-neutral contracts:
  - `IntegrationEvent` + `IEventSubscriber<T>` (Abstractions) for asynchronous
    after events. Events travel as a JSON `EventEnvelope`; the dispatcher runs
    every subscriber inside the event's tenant (and user).
  - `IOutbox.SaveChangesAsync(db, events, messages)` for atomic save + publish.
  - `ITenantMessage` for background messages (e.g. `RunOperation`).
- **Delivery policy:** durable local queues; retries with cooldown (100 ms,
  500 ms, 2 s), then scheduled retries (10 s, 1 min, 5 min), then the
  dead-letter table. Subscribers must be idempotent.
- **Before events** stay synchronous inside the item write path
  (`ItemWriter` + `IItemEventReceiver`), not in Wolverine.
- Handlers are compiled at runtime (`WolverineFx.RuntimeCompilation`, Roslyn).
  Pre-generated (static) handler code can replace it later to cut startup time.
- Hosted-service order matters: the bootstrap/migration service is registered
  first so the database exists before Wolverine builds its storage.

## Consequences
- Every module message/event is a contract; Wolverine can be swapped without
  touching modules.
- Recurring (cron) jobs are not a Wolverine feature; see ADR-0010.
