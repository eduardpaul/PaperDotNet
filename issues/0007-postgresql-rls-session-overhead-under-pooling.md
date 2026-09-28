# 0007: PostgreSQL RLS tenant-setting overhead under connection pooling is unmeasured

- **Status:** possible
- **Area:** Other
- **Date:** 2026-09-27

## Problem

[ADR-0013](../docs/adr/0013-openiddict-passkeys-rls.md) notes row-level
security costs "one extra round trip per PostgreSQL connection open"
(`set_config` of `app.tenant_id`). That framing assumes the round trip is
paid once per physical connection. Under a connection pooler in transaction
mode (e.g. PgBouncer, common for scaling many concurrent users against a
single PostgreSQL instance), a "connection" from the app's point of view can
map to a new pooled session per transaction, which would make this a
per-transaction cost instead of a per-connection one — i.e., on every
request, not just at startup or pool growth.

## Where it shows up

- [ADR-0013](../docs/adr/0013-openiddict-passkeys-rls.md), "Consequences"
  section.
- Whatever connection interceptor sets `app.tenant_id` per EF Core
  `DbConnection` open (RLS support in `Persistence.PostgreSql`).

## Possible approaches

- Measure the actual per-request cost of the `set_config` round trip with a
  realistic pooling setup (PgBouncer transaction mode) rather than a direct
  connection, since that is the deployment shape "lots of concurrent users"
  implies.
- If it is material, consider session-mode pooling specifically for this
  workload, or batching the `set_config` into the same round trip as the
  first query of the transaction instead of a separate one.

**This needs more investigation before scheduling:** nothing here has been
measured; it's a reading of the ADR's own wording against a deployment shape
(pooled PostgreSQL under many concurrent users) that the ADR doesn't appear
to have been written against. A test with PgBouncer in front of PostgreSQL,
compared to a direct connection, would confirm whether this is real before
any change is made.

## Update (2026-09-28)

From reading the code: `TenantSessionInterceptor` runs on every
`ConnectionOpened`. EF Core opens and closes the connection for each query
outside a transaction, so the extra round trip is per query already, even
without a pooler. Under a transaction-mode pooler there is also a correctness
problem: `set_config(…, false)` and the next query run as separate
transactions and can land on different server connections, so the query can
run with another request's tenant setting. RLS then hides rows that the EF
filter expects. The setting has to travel with the query (in the same
transaction or the same batch), or the connection has to stay open for the
request.
