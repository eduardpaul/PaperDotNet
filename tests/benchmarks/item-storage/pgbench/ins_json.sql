INSERT INTO bench.w_json(id, tenant_id, list_id, content_type_id, is_folder, has_unique_permissions, title, fields, created_at, updated_at, version)
VALUES (gen_random_uuid(), md5('tenant')::uuid, md5('list-crm')::uuid, md5('ct-deal')::uuid, false, false, 'New deal',
 jsonb_build_object('stage','Lead','amount',1000,'closeDate','2025-03-01','assignedTo',jsonb_build_array(md5('u3')::uuid::text),'account',md5('acc9')::uuid::text), now(), now(), 1);
