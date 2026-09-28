-- Field storage candidates: typed slot columns on items (S) and a typed pivot index table (V).
SET search_path = bench;
-- Option S2: typed slot columns on items (filled from promoted fields), one index per slot.
ALTER TABLE items ADD COLUMN owner_id uuid, ADD COLUMN s_text1 varchar(255), ADD COLUMN s_num1 numeric, ADD COLUMN s_date1 date;
UPDATE items SET owner_id = created_by,
  s_text1 = coalesce(fields->>'stage', fields->>'status'),
  s_num1  = (fields->>'amount')::numeric,
  s_date1 = coalesce(fields->>'closeDate', fields->>'dueDate')::date
WHERE list_id = md5('list-crm')::uuid OR content_type_id = md5('ct-task')::uuid;
CREATE INDEX ix_items_s_text1 ON items(list_id, s_text1, id) WHERE s_text1 IS NOT NULL;
CREATE INDEX ix_items_s_num1  ON items(list_id, s_num1, id)  WHERE s_num1 IS NOT NULL;
CREATE INDEX ix_items_s_date1 ON items(list_id, s_date1, id) WHERE s_date1 IS NOT NULL;
CREATE INDEX ix_items_owner   ON items(list_id, owner_id, id) WHERE owner_id IS NOT NULL;

-- Option S3: typed pivot index (one row per promoted value; multi-valued fields give several rows).
CREATE TABLE item_values(
  tenant_id uuid NOT NULL, list_id uuid NOT NULL, item_id uuid NOT NULL, field varchar(100) NOT NULL, ordinal smallint NOT NULL DEFAULT 0,
  v_text varchar(255), v_num numeric, v_date date, v_guid uuid,
  PRIMARY KEY (item_id, field, ordinal));
INSERT INTO item_values(tenant_id, list_id, item_id, field, v_text)
  SELECT tenant_id, list_id, id, 'stage', fields->>'stage' FROM items WHERE fields ? 'stage'
  UNION ALL SELECT tenant_id, list_id, id, 'status', fields->>'status' FROM items WHERE fields ? 'status';
INSERT INTO item_values(tenant_id, list_id, item_id, field, v_num)
  SELECT tenant_id, list_id, id, 'amount', (fields->>'amount')::numeric FROM items WHERE fields ? 'amount';
INSERT INTO item_values(tenant_id, list_id, item_id, field, v_date)
  SELECT tenant_id, list_id, id, 'closeDate', (fields->>'closeDate')::date FROM items WHERE fields ? 'closeDate'
  UNION ALL SELECT tenant_id, list_id, id, 'dueDate', (fields->>'dueDate')::date FROM items WHERE fields ? 'dueDate';
INSERT INTO item_values(tenant_id, list_id, item_id, field, ordinal, v_guid)
  SELECT i.tenant_id, i.list_id, i.id, 'assignedTo', (a.ord - 1)::smallint, a.v::uuid FROM items i, jsonb_array_elements_text(i.fields->'assignedTo') WITH ORDINALITY a(v, ord) WHERE i.fields ? 'assignedTo'
  UNION ALL SELECT tenant_id, list_id, id, 'account', 0, (fields->>'account')::uuid FROM items WHERE fields ? 'account';
CREATE INDEX ix_values_text ON item_values(list_id, field, v_text, item_id) WHERE v_text IS NOT NULL;
CREATE INDEX ix_values_num  ON item_values(list_id, field, v_num, item_id)  WHERE v_num IS NOT NULL;
CREATE INDEX ix_values_date ON item_values(list_id, field, v_date, item_id) WHERE v_date IS NOT NULL;
CREATE INDEX ix_values_guid ON item_values(field, v_guid, list_id, item_id) WHERE v_guid IS NOT NULL;
VACUUM ANALYZE;
\echo @@ sizes
SELECT count(*) values_rows, pg_size_pretty(pg_total_relation_size($$item_values$$)) values_size FROM item_values;
