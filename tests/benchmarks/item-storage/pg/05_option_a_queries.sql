-- Option A/C read path. Run with -v user=uN -v n=N.
SET search_path = bench;
SELECT md5('list-dms')::uuid AS dms, md5('tenant')::uuid AS t \gset
SELECT '{' || md5(:'user')::uuid || ',' || md5('ws-members')::uuid || ',' || coalesce(string_agg(group_id::text, ','), '') || '}' AS principals
  FROM group_members WHERE user_id = md5(:'user')::uuid \gset
SELECT '{' || string_agg(scope_id::text, ',') || '}' AS allowed FROM acl
 WHERE principal_id = ANY(:'principals'::uuid[]) AND list_id = :'dms' AND level >= 1 \gset
\echo @@ A allowed set from the ACL by principal (replaces preload 1-4)
EXPLAIN (ANALYZE, COSTS OFF) SELECT scope_id, max(level) FROM acl WHERE principal_id = ANY(:'principals'::uuid[]) AND list_id = :'dms' GROUP BY scope_id;
\echo @@ C allowed set from the per-user table
EXPLAIN (ANALYZE, COSTS OFF) SELECT scope_id, level FROM user_access WHERE user_id = md5(:'user')::uuid AND list_id = :'dms';
\echo @@ A array Q1 own sub-folder page
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL
  AND parent_id = md5('sf' || :'n' || '-1')::uuid AND scope2 = ANY(:'allowed'::uuid[]) ORDER BY is_folder DESC, title LIMIT 101;
\echo @@ A array Q2 root page
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL
  AND parent_id IS NULL AND scope2 = ANY(:'allowed'::uuid[]) ORDER BY is_folder DESC, title LIMIT 101;
\echo @@ A array Q3 all readable documents by id
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL AND NOT is_folder
  AND scope2 = ANY(:'allowed'::uuid[]) ORDER BY id LIMIT 101;
\echo @@ A array Q4 count readable documents
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL AND NOT is_folder
  AND scope2 = ANY(:'allowed'::uuid[]);
\echo @@ A semi-join Q2 root page
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL AND parent_id IS NULL
  AND scope2 IN (SELECT scope_id FROM acl WHERE principal_id = ANY(:'principals'::uuid[]) AND list_id = :'dms' AND level >= 1)
  ORDER BY is_folder DESC, title LIMIT 101;
\echo @@ A semi-join Q3 all readable documents by id
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'dms' AND deleted_at IS NULL AND NOT is_folder
  AND scope2 IN (SELECT scope_id FROM acl WHERE principal_id = ANY(:'principals'::uuid[]) AND list_id = :'dms' AND level >= 1)
  ORDER BY id LIMIT 101;
