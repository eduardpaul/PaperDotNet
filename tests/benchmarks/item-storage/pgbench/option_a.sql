\set n random(1, 5000)
SELECT coalesce(array_agg(scope_id), '{}')::text AS allowed FROM bench.acl WHERE principal_id = ANY (ARRAY[md5('u' || :n)::uuid, md5('ws-members')::uuid] || ARRAY(SELECT group_id FROM bench.group_members WHERE user_id = md5('u' || :n)::uuid)) AND list_id = md5('list-dms')::uuid AND level >= 1 \gset
SELECT * FROM bench.items WHERE tenant_id = md5('tenant')::uuid AND list_id = md5('list-dms')::uuid AND deleted_at IS NULL AND parent_id = md5('sf' || :n || '-1')::uuid AND scope2 = ANY (':allowed'::uuid[]) ORDER BY is_folder DESC, title LIMIT 101;
