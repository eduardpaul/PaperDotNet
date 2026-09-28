-- The same filters as 13, written as IN (subquery): what EF generates for values.Select(v => v.ItemId).Contains(i.Id).
SET search_path = multi;
SELECT md5('list-multi')::uuid AS l, md5('c123')::uuid AS contact, md5('labels:Label3')::uuid AS a, md5('labels:Label5')::uuid AS b, md5('labels:Rare')::uuid AS rare \gset
\echo @@ M1 compact IN any of two common labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 1 AND v.value IN (:'a', :'b')) ORDER BY id LIMIT 101;
\echo @@ M2 compact IN rare label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 1 AND v.value = :'rare') ORDER BY id LIMIT 101;
\echo @@ M3 compact IN all of two labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 1 AND v.value = :'a')
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 1 AND v.value = :'b') ORDER BY id LIMIT 101;
\echo @@ M4 compact NOT IN none of a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND id NOT IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 1 AND v.value = :'a') ORDER BY id LIMIT 101;
\echo @@ M6 compact IN items referencing one contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 2 AND v.value = :'contact') ORDER BY id LIMIT 101;
\echo @@ M7 compact IN label and contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 1 AND v.value = :'a')
  AND id IN (SELECT item_id FROM item_values_compact v WHERE v.list_id = :'l' AND v.field = 2 AND v.value = :'contact') ORDER BY id LIMIT 101;
