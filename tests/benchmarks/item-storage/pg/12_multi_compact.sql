-- Compact value table: field as a small number, every value as a uuid (choices as a name-based uuid of field + text).
SET search_path = multi;
DROP TABLE IF EXISTS item_values_compact;
CREATE TABLE item_values_compact(tenant_id uuid NOT NULL, list_id uuid NOT NULL, item_id uuid NOT NULL, field smallint NOT NULL, value uuid NOT NULL,
  PRIMARY KEY (item_id, field, value));
INSERT INTO item_values_compact
SELECT tenant_id, list_id, item_id, CASE field WHEN 'labels' THEN 1 ELSE 2 END,
       CASE field WHEN 'labels' THEN md5('labels:' || value)::uuid ELSE value::uuid END
FROM item_values;
CREATE INDEX ix_values_compact_lookup ON item_values_compact(list_id, field, value, item_id);
VACUUM ANALYZE item_values_compact;
\echo @@ sizes
SELECT (SELECT count(*) FROM item_values_compact) value_rows, pg_size_pretty(pg_total_relation_size('item_values_compact')) value_size;
