# ADR-0026: Live events across servers with PostgreSQL LISTEN/NOTIFY

- **Status:** Accepted (closes the open point of ADR-0025)
- **Date:** 2026-09-25

## Context

Live events (`GET /v1.0/me/events`, server-sent events: operation progress,
document processing, new notifications) were held in memory on each server. A
client connected to server B missed events raised on server A, for example
when OCR ran on the server that received the upload.

Live events are best effort by design: clients reconnect and re-read state,
and durable reactions use integration events. A delivery mechanism across
servers therefore only needs to be cheap and fast, not durable. PostgreSQL
already offers that with `LISTEN`/`NOTIFY`, with no new infrastructure.

## Decision

- **Abstraction.** `ILiveEventBackplane` (PaperDotNet.Abstractions) carries
  messages between servers and is implemented by a database provider.
  `LiveEventHub`:
  - delivers an event to its own subscribers right away;
  - sends it to the backplane as JSON: the server id, type, tenant, user and
    the data serialized with the API's JSON options, so clients on every server
    get identical output;
  - delivers what other servers send, and skips its own messages.
- **PostgreSQL** (`PostgreSqlLiveEventBackplane`, a hosted service in the
  PostgreSQL plugin):
  - one connection per server `LISTEN`s on `paperdotnet_live_events` and
    reconnects with backoff (1 to 30 seconds);
  - a background loop sends messages in order with `pg_notify`, from a bounded
    queue (10,000, dropping the oldest), so publishing never blocks;
  - events over about 7.9 KB (PostgreSQL's limit is 8,000 bytes) stay on their
    server, with a warning.
- **SQLite** has no backplane: it runs on one server, and events stay in the
  process as before.

## Consequences

- With PostgreSQL, any number of servers behind a load balancer serve live
  events to any client. The cost is one extra connection per server and one
  round trip per event.
- Best effort: events sent while a server is reconnecting are lost; clients
  catch up by re-reading state.
- `LISTEN` needs a session-level connection. Behind PgBouncer, use session
  pooling (or a direct connection) for PaperDotNet; transaction pooling
  breaks it.
