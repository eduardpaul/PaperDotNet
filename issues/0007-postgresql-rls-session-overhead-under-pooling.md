# 0007: PostgreSQL RLS tenant-setting overhead under connection pooling is unmeasured

- **Status:** done
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

Measured through the real `ListsDbContext` (EF Core 10.0.12, Npgsql 10.0.3,
[`tests/benchmarks/item-storage/ef`](../tests/benchmarks/item-storage/ef/run-ef.sh)):
five queries outside a transaction opened five connections, and PostgreSQL
logged five `SELECT set_config('app.tenant_id', $1, false)`. The same five
queries in one transaction opened one connection. So the extra round trip is
paid per query today, even without a pooler: EF Core opens and closes the
connection for each query outside a transaction, and
`TenantSessionInterceptor` runs on every open.

Under a transaction-mode pooler there is also a correctness problem (not
tested): `set_config(…, false)` and the next query run as separate
transactions and can land on different server connections, so a query can
run with another request's tenant setting. RLS then hides rows that the EF
filter expects. The setting has to travel with the query (in the same
transaction or the same batch), or the connection has to stay open for the
request.

## Done (2026-09-29)

[ADR-0035](../docs/adr/0035-item-storage-and-permissions-at-scale.md) step 6.
The setting travels with the query: a command outside a transaction is sent
as `SET app.tenant_id = '<id>'; <query>` in one batch (one round trip, one
implicit transaction, so safe behind a transaction-mode pooler), a
transaction runs `SET LOCAL app.tenant_id` at its start, and saves always run
in a transaction. `PaperDotNet.Performance` on PostgreSQL, eight callers:
reads 510 → 662 requests/s, shared-folder pages 450 → 625. Not yet tested
behind PgBouncer.
