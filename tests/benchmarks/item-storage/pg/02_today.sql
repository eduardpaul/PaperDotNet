-- Today's per-request cost (ADR-0011 as implemented): the four ListSchemaLoader.GetAccessAsync queries,
-- then the page queries with the allowed scope ids as an array. Run with -v user=uN -v n=N.
SET search_path = bench;
SELECT md5('list-dms')::uuid AS dms, md5('tenant')::uuid AS t \gset
SELECT '{' || coalesce(string_agg(object_id::text, ','), '') || '}' AS allowed FROM permission_grants
 WHERE list_id = :'dms' AND ((principal_type = 'User' AND principal_id = md5(:'user')::uuid)
    OR (principal_type = 'Group' AND principal_id IN (SELECT group_id FROM group_members WHERE user_id = md5(:'user')::uuid))) \gset
\echo @@ today preload 1: any unique item scope in the list
EXPLAIN (ANALYZE, COSTS OFF) SELECT EXISTS (SELECT 1 FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND has_unique_permissions);
\echo @@ today preload 2: groups of the user
EXPLAIN (ANALYZE, COSTS OFF) SELECT group_id FROM group_members WHERE user_id = md5(:'user')::uuid;
\echo @@ today preload 3: grants of the user in the list
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM permission_grants WHERE tenant_id = :'t' AND list_id = :'dms'
  AND ((principal_type = 'User' AND principal_id = md5(:'user')::uuid)
       OR (principal_type = 'Group' AND principal_id IN (SELECT group_id FROM group_members WHERE user_id = md5(:'user')::uuid)));
\echo @@ today preload 4: every unique scope id of the list
EXPLAIN (ANALYZE, COSTS OFF) SELECT id FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND has_unique_permissions;
\echo @@ today Q1 own sub-folder page
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL
  AND parent_id = md5('sf' || :'n' || '-1')::uuid AND (scope_id IS NULL OR scope_id = ANY(:'allowed'::uuid[]))
  ORDER BY is_folder DESC, title LIMIT 101;
\echo @@ today Q2 root page
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL
  AND parent_id IS NULL AND (scope_id IS NULL OR scope_id = ANY(:'allowed'::uuid[]))
  ORDER BY is_folder DESC, title LIMIT 101;
\echo @@ today Q3 all readable documents by id
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL AND NOT is_folder
  AND (scope_id IS NULL OR scope_id = ANY(:'allowed'::uuid[])) ORDER BY id LIMIT 101;
\echo @@ today Q4 count readable documents
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL AND NOT is_folder
  AND (scope_id IS NULL OR scope_id = ANY(:'allowed'::uuid[]));
