-- The same 500k CRM rows in three layouts, for write throughput (pgbench/upd_*.sql, ins_*.sql).
SET search_path = bench;
DROP TABLE IF EXISTS w_json, w_slots, w_pivot, w_pivot_values;
-- Same 500k CRM rows in three layouts.
CREATE TABLE w_json AS SELECT id, tenant_id, list_id, content_type_id, parent_id, is_folder, has_unique_permissions, scope_id, title, fields,
  created_at, created_by, updated_at, updated_by, deleted_at, deleted_by, version FROM items WHERE list_id = md5('list-crm')::uuid;
ALTER TABLE w_json ADD PRIMARY KEY (id);
CREATE INDEX ON w_json(tenant_id); CREATE INDEX ON w_json(list_id, parent_id); CREATE INDEX ON w_json(list_id, scope_id);
CREATE INDEX ON w_json USING gin (fields jsonb_path_ops);

CREATE TABLE w_slots AS SELECT id, tenant_id, list_id, content_type_id, parent_id, is_folder, has_unique_permissions, scope2 AS scope_id, title, fields,
  created_at, created_by, updated_at, updated_by, deleted_at, deleted_by, version, s_text1, s_num1, s_date1 FROM items WHERE list_id = md5('list-crm')::uuid;
ALTER TABLE w_slots ADD PRIMARY KEY (id);
CREATE INDEX ON w_slots(tenant_id); CREATE INDEX ON w_slots(list_id, parent_id); CREATE INDEX ON w_slots(scope_id, id);
CREATE INDEX ON w_slots USING gin (fields jsonb_path_ops);
CREATE INDEX ON w_slots(list_id, s_text1, id) WHERE s_text1 IS NOT NULL;
CREATE INDEX ON w_slots(list_id, s_num1, id) WHERE s_num1 IS NOT NULL;
CREATE INDEX ON w_slots(list_id, s_date1, id) WHERE s_date1 IS NOT NULL;

CREATE TABLE w_pivot AS SELECT * FROM w_json;
ALTER TABLE w_pivot ADD PRIMARY KEY (id);
CREATE INDEX ON w_pivot(tenant_id); CREATE INDEX ON w_pivot(list_id, parent_id); CREATE INDEX ON w_pivot(list_id, scope_id);
CREATE INDEX ON w_pivot USING gin (fields jsonb_path_ops);
CREATE TABLE w_pivot_values AS SELECT * FROM item_values WHERE list_id = md5('list-crm')::uuid;
ALTER TABLE w_pivot_values ADD PRIMARY KEY (item_id, field, ordinal);
CREATE INDEX ON w_pivot_values(list_id, field, v_text, item_id) WHERE v_text IS NOT NULL;
CREATE INDEX ON w_pivot_values(list_id, field, v_num, item_id) WHERE v_num IS NOT NULL;
CREATE INDEX ON w_pivot_values(list_id, field, v_date, item_id) WHERE v_date IS NOT NULL;
CREATE INDEX ON w_pivot_values(field, v_guid, list_id, item_id) WHERE v_guid IS NOT NULL;
VACUUM ANALYZE w_json; VACUUM ANALYZE w_slots; VACUUM ANALYZE w_pivot; VACUUM ANALYZE w_pivot_values;
