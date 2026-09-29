-- Option A: every item has a scope (the list id when it inherits from the list) and the ACL is indexed by principal.
-- Option C: the same access expanded per user (groups and workspace membership resolved ahead of time).
SET search_path = bench;
ALTER TABLE items ADD COLUMN scope2 uuid;
UPDATE items SET scope2 = coalesce(scope_id, list_id);
ALTER TABLE items ALTER COLUMN scope2 SET NOT NULL;
CREATE INDEX ix_items_scope2_id ON items(scope2, id);
CREATE INDEX ix_items_browse ON items(list_id, parent_id, is_folder DESC, title, id);
DROP INDEX ix_items_list_scope;  -- replaced by (scope2, id)

CREATE TABLE acl(tenant_id uuid NOT NULL, list_id uuid NOT NULL, scope_id uuid NOT NULL, principal_id uuid NOT NULL, level smallint NOT NULL,
                 PRIMARY KEY (scope_id, principal_id));
INSERT INTO acl SELECT tenant_id, list_id, object_id, principal_id,
       CASE level WHEN 'Read' THEN 1 WHEN 'Contribute' THEN 2 ELSE 3 END FROM permission_grants;
-- Lists that inherit from the workspace: one entry per workspace-role pseudo-principal (here: members -> Contribute).
INSERT INTO acl SELECT DISTINCT md5('tenant')::uuid, list_id, list_id, md5('ws-members')::uuid, 2 FROM items;
CREATE INDEX ix_acl_principal ON acl(principal_id, list_id, scope_id, level);

CREATE TABLE user_access(user_id uuid NOT NULL, list_id uuid NOT NULL, scope_id uuid NOT NULL, level smallint NOT NULL,
                         PRIMARY KEY (user_id, list_id, scope_id));
INSERT INTO user_access
SELECT user_id, list_id, scope_id, max(level) FROM (
  SELECT a.principal_id user_id, a.list_id, a.scope_id, a.level FROM acl a JOIN users u ON u.id = a.principal_id
  UNION ALL
  SELECT gm.user_id, a.list_id, a.scope_id, a.level FROM acl a JOIN group_members gm ON gm.group_id = a.principal_id
  UNION ALL
  SELECT u.id, a.list_id, a.scope_id, a.level FROM acl a CROSS JOIN users u WHERE a.principal_id = md5('ws-members')::uuid
) x GROUP BY 1, 2, 3;
VACUUM ANALYZE;
\echo @@ sizes
SELECT (SELECT count(*) FROM acl) acl_rows, pg_size_pretty(pg_total_relation_size('acl')) acl_size,
       (SELECT count(*) FROM user_access) user_access_rows, pg_size_pretty(pg_total_relation_size('user_access')) user_access_size;
