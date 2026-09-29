\set r random(1, 2000000000)
BEGIN;
INSERT INTO bench.w_pivot(id, tenant_id, list_id, content_type_id, is_folder, has_unique_permissions, title, fields, created_at, updated_at, version)
VALUES (md5('new' || :r || clock_timestamp())::uuid, md5('tenant')::uuid, md5('list-crm')::uuid, md5('ct-deal')::uuid, false, false, 'New deal',
 jsonb_build_object('stage','Lead','amount',1000,'closeDate','2025-03-01','assignedTo',jsonb_build_array(md5('u3')::uuid::text),'account',md5('acc9')::uuid::text), now(), now(), 1)
RETURNING id \gset
INSERT INTO bench.w_pivot_values(tenant_id, list_id, item_id, field, ordinal, v_text, v_num, v_date, v_guid) VALUES
 (md5('tenant')::uuid, md5('list-crm')::uuid, ':id', 'stage', 0, 'Lead', NULL, NULL, NULL),
 (md5('tenant')::uuid, md5('list-crm')::uuid, ':id', 'amount', 0, NULL, 1000, NULL, NULL),
 (md5('tenant')::uuid, md5('list-crm')::uuid, ':id', 'closeDate', 0, NULL, NULL, '2025-03-01', NULL),
 (md5('tenant')::uuid, md5('list-crm')::uuid, ':id', 'assignedTo', 0, NULL, NULL, NULL, md5('u3')::uuid),
 (md5('tenant')::uuid, md5('list-crm')::uuid, ':id', 'account', 0, NULL, NULL, NULL, md5('acc9')::uuid);
COMMIT;
