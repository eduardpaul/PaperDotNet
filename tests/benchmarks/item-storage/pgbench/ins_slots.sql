INSERT INTO bench.w_slots(id, tenant_id, list_id, content_type_id, is_folder, has_unique_permissions, scope_id, title, fields, created_at, updated_at, version, owner_id, s_text1, s_num1, s_date1)
VALUES (gen_random_uuid(), md5('tenant')::uuid, md5('list-crm')::uuid, md5('ct-deal')::uuid, false, false, md5('list-crm')::uuid, 'New deal',
 jsonb_build_object('stage','Lead','amount',1000,'closeDate','2025-03-01','assignedTo',jsonb_build_array(md5('u3')::uuid::text),'account',md5('acc9')::uuid::text), now(), now(), 1,
 md5('u3')::uuid, 'Lead', 1000, '2025-03-01');
