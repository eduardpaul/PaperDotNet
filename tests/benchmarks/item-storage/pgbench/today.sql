\set n random(1, 5000)
SELECT EXISTS (SELECT 1 FROM bench.items WHERE tenant_id = md5('tenant')::uuid AND list_id = md5('list-dms')::uuid AND has_unique_permissions);
SELECT coalesce(array_agg(group_id), '{}')::text AS groups FROM bench.group_members WHERE user_id = md5('u' || :n)::uuid \gset
SELECT coalesce(array_agg(object_id), '{}')::text AS allowed FROM bench.permission_grants WHERE tenant_id = md5('tenant')::uuid AND list_id = md5('list-dms')::uuid AND ((principal_type = 'User' AND principal_id = md5('u' || :n)::uuid) OR (principal_type = 'Group' AND principal_id = ANY (':groups'::uuid[]))) \gset
SELECT id FROM bench.items WHERE tenant_id = md5('tenant')::uuid AND list_id = md5('list-dms')::uuid AND has_unique_permissions;
SELECT * FROM bench.items WHERE tenant_id = md5('tenant')::uuid AND list_id = md5('list-dms')::uuid AND deleted_at IS NULL AND parent_id = md5('sf' || :n || '-1')::uuid AND (scope_id IS NULL OR scope_id = ANY (':allowed'::uuid[])) ORDER BY is_folder DESC, title LIMIT 101;
