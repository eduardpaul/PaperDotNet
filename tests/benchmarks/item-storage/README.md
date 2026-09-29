# Item and permission storage benchmark

The measurements behind [item-and-permission-storage.md](../../../docs/item-and-permission-storage.md).
It compares query and write shapes directly in PostgreSQL and SQLite, on tables
shaped like the ones EF Core creates for `lists.items` and
`lists.permission_grants`. It does not run the application: request overhead,
EF Core and the network are not included.

## Data

One tenant, one workspace, 5,000 users, 50 groups (every user is in two):

| List | Rows | Shape |
|---|---:|---|
| Library (DMS) | 1,035,001 | Laid out like a Papermerge import: one home folder per user with unique permissions (owner: Manage), 4 sub-folders of 50 documents each. Every fifth home folder is also shared with a group (Read). 2,500 documents are shared with one more user (their own unique scope). A `Public` folder with 10,000 documents inherits from the list. 7,500 unique scopes in total |
| CRM deals | 500,000 | No folders. Fields `stage` (6 values), `amount`, `closeDate`, `assignedTo` (one of 200 sales users), `account` (lookup, 20,000 accounts) |
| Tasks | 50 × 2,000 | Fields `status`, `dueDate`, `assignedTo` (1,000 users) |

Two users are measured: `u1` reads its own home folder and the `Public`
folder (3 allowed scopes). `u5` also reads 200 home folders through its groups
(202 allowed scopes, about 50,000 documents).

## Candidates

- **Today:** the four access queries of `ListSchemaLoader.GetAccessAsync`,
  then the page query with the allowed scope ids.
- **Minimal fix:** the same, with a partial index for the unique-scope query.
- **A:** every item has a scope (the list id when it inherits); an ACL table
  indexed by principal gives the allowed scopes in one index-only query.
- **C:** the same access expanded per user ahead of time.
- **Fields:** JSON only (today), typed slot columns on `items`, a typed pivot
  table (`item_values`), and the pivot used only for multi-valued references.

## Running it

Needs PostgreSQL 16 or later with `psql` and `pgbench`, and Python 3 (its
`sqlite3` module). The scripts create and drop the schema `bench` in the
database of the usual `PG*` variables. Allow about 4 GB of free disk (the
SQLite copy and its CSV export) and 15 minutes.

```bash
PGHOST=localhost PGPORT=5432 PGUSER=postgres PGDATABASE=postgres \
  OUT=/tmp/item-storage ./tests/benchmarks/item-storage/run.sh
```

Every result line starts with `@@` and is collected in `$OUT/results.txt`.
`RUN_SECONDS` changes the length of each pgbench run (default 8), `PGBENCH`
the pgbench binary.

## EF Core check

`ef/run-ef.sh` runs the permission queries through the real `ListsDbContext`
(registered like the host, with the tenant filter and the RLS interceptor) on
the same data in EF's own schema: it prints the SQL EF generates on both
providers, times today's access queries and option A's shapes (`Contains`, a
subquery, `EF.Parameter`), and counts connection opens. Needs the .NET 10 SDK
as well.

```bash
PGHOST=localhost PGPORT=5432 PGUSER=postgres OUT=/tmp/item-storage-ef \
  ./tests/benchmarks/item-storage/ef/run-ef.sh
```

It creates the database `efprobe`. The probe (`ef/EfProbe.csproj`) is not part
of the solution and has its own `Directory.Build.props`, so the repository's
analyzers do not apply to it.

## Files

| File | What it does |
|---|---|
| `pg/01_data.sql` | Schema and data, with today's indexes |
| `pg/02_today.sql` | Today's access queries and page queries (`-v user=uN -v n=N`) |
| `pg/03_minimal_fix.sql` | Partial index for the unique-scope query |
| `pg/04_option_a.sql` | Scope column, ACL table and per-user table |
| `pg/05_option_a_queries.sql` | Allowed-scope lookups and page queries for A |
| `pg/06_fields.sql`, `pg/07_field_queries.sql` | Slot columns, pivot table, and field queries |
| `pg/08_write_tables.sql`, `pgbench/upd_*.sql`, `pgbench/ins_*.sql` | Write throughput per layout |
| `pg/09_fanout.sql` | Rewriting the scope of a subtree |
| `pgbench/today.sql`, `pgbench/option_a.sql` | One list page request (access + page) for a random user |
| `sqlite/load.py`, `sqlite/bench.py` | The same rows in SQLite (EF-style TEXT GUIDs), the same queries, and the write-lock test |
| `pg/10`–`14_multi_*.sql`, `sqlite/multi.py` | Multi-select and multi-lookup fields: JSON vs. a value table (text and compact uuid), `EXISTS` vs. `IN` |
| `pg/15_slots30.sql`, `pgbench/*_base.sql`, `*_slots3.sql`, `*_slots30.sql` | Write cost of 10 text, 10 number and 10 date slots, by slots filled |
| `ef/` | EF Core check: probe console, SQLite loader from EF's DDL, option A data shape |
