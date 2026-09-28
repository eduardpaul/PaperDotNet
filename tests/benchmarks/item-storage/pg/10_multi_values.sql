-- Multi-select (choice) and multi-lookup fields: JSON as today vs. one value table (choice text and ids in one column).
-- 500,000 items; "labels": 1-4 of 11 common labels (~20% each) plus "Rare" on 0.5%; "contacts": 1-5 of 50,000 ids.
DROP SCHEMA IF EXISTS multi CASCADE;
CREATE SCHEMA multi;
SET search_path = multi;
CREATE TABLE items(id uuid PRIMARY KEY, tenant_id uuid NOT NULL, list_id uuid NOT NULL, deleted_at timestamptz, title text NOT NULL, fields jsonb NOT NULL);
INSERT INTO items
SELECT md5('m' || n)::uuid, md5('tenant')::uuid, md5('list-multi')::uuid, NULL, 'Item ' || n,
  jsonb_build_object(
    'labels', (SELECT jsonb_agg(DISTINCT l) FROM (
        SELECT 'Label' || ((n * k * 7 + k) % 11) AS l FROM generate_series(1, 1 + n % 4) k
        UNION ALL SELECT 'Rare' WHERE n % 200 = 0) x),
    'contacts', (SELECT jsonb_agg(DISTINCT md5('c' || ((n * 31 + k * 9973) % 50000))::uuid::text) FROM generate_series(1, 1 + n % 5) k))
FROM generate_series(1, 500000) n;
CREATE INDEX ix_items_list ON items(list_id);
CREATE INDEX ix_items_fields ON items USING gin (fields jsonb_path_ops);

CREATE TABLE item_values(tenant_id uuid NOT NULL, list_id uuid NOT NULL, item_id uuid NOT NULL, field varchar(100) NOT NULL, value varchar(255) NOT NULL,
  PRIMARY KEY (item_id, field, value));
INSERT INTO item_values
SELECT i.tenant_id, i.list_id, i.id, f.key, v.value FROM items i, jsonb_each(i.fields) f, jsonb_array_elements_text(f.value) v;
CREATE INDEX ix_values_lookup ON item_values(list_id, field, value, item_id);
VACUUM ANALYZE;
\echo @@ sizes
SELECT (SELECT count(*) FROM item_values) value_rows, pg_size_pretty(pg_total_relation_size('item_values')) value_size,
       pg_size_pretty(pg_relation_size('ix_items_fields')) gin_size;
