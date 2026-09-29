-- Option A's data shape on EF's schema: every item has a scope, list scopes get a workspace-members entry,
-- the scope index replaces (list_id, scope_id), and grants are indexed by principal.
SET search_path = lists;
UPDATE items SET scope_id = list_id WHERE scope_id IS NULL;
INSERT INTO permission_grants SELECT md5('ws' || list_id::text)::uuid, md5('tenant')::uuid, list_id, list_id, 'Group', md5('ws-members')::uuid, 'Contribute'
  FROM (SELECT DISTINCT list_id FROM items) l;
DROP INDEX ix_items_list_scope;
CREATE INDEX ix_items_scope_id_id ON items(scope_id, id);
CREATE INDEX ix_grants_principal ON permission_grants(principal_id, list_id, object_id);
VACUUM ANALYZE items;
VACUUM ANALYZE permission_grants;
