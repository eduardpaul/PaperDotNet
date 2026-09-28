-- Permission fan-out: rewriting the scope of a subtree (break/reset inheritance, folder move). Rolled back.
SET search_path = bench;
\timing on
\echo @@ scope rewrite 10,000 rows (break inheritance on the Public folder), one statement
BEGIN;
UPDATE items SET scope_id = md5('pub')::uuid, scope2 = md5('pub')::uuid WHERE parent_id = md5('pub')::uuid;
ROLLBACK;
\echo @@ scope rewrite 100,000 rows (subtree of 500 home folders), one statement
BEGIN;
UPDATE items SET scope_id = md5('x')::uuid, scope2 = md5('x')::uuid
 WHERE parent_id IN (SELECT id FROM items WHERE parent_id IN (SELECT md5('hf'||i)::uuid FROM generate_series(1,500) i));
ROLLBACK;
