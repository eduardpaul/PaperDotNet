# ADR-0008: Wolverine for reliable events (outbox)

**Status:** Accepted (2026-09-24); implemented in phase 1c.

## Context
After-event handlers (idea 0012), search indexing, webhooks, notifications
and automation need at-least-once delivery tied to the database transaction.
The options were our own outbox (+ Channels) or Wolverine; the user chose
Wolverine.

## Decision
- Use **Wolverine** (MIT) with its **PostgreSQL message storage and EF Core
  transactional outbox**: messages are stored in the same database and
  transaction as the data. No broker is required, so the self-hosting
  footprint stays "app + PostgreSQL".
- Local (in-process) durable queues deliver to handlers with retries,
  scheduled retries and dead-lettering. The same messages can later go to a
  broker if PaperDotNet is scaled out.
- Before-event handlers stay synchronous inside the item write path
  (`ItemWriter`), not in Wolverine.
- Quartz.NET remains the choice for cron-style schedules unless Wolverine's
  scheduling covers the need (decided in 1c).
- Every message carries `TenantId`; handlers run inside a tenant scope.

## Consequences
- Handler code follows Wolverine conventions (message types and handlers
  discovered by convention). Module boundaries still apply: messages are
  contracts.
- Technical approach §7 is updated accordingly.
