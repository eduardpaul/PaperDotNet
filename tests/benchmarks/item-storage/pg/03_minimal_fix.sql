-- "Keep ADR-0011, patch it": an index for the unique-scope preload.
SET search_path = bench;
CREATE INDEX IF NOT EXISTS ix_items_unique ON items(list_id, id) WHERE has_unique_permissions;
ANALYZE items;
\echo @@ minimal fix: preload 4 with a partial index
EXPLAIN (ANALYZE, COSTS OFF) SELECT id FROM items WHERE tenant_id = md5('tenant')::uuid AND list_id = md5('list-dms')::uuid AND has_unique_permissions;
