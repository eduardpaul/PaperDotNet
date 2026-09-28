SET search_path = multi;
SELECT md5('list-multi')::uuid AS l, md5('c123')::uuid AS contact, md5('labels:Label3')::uuid AS a, md5('labels:Label5')::uuid AS b, md5('labels:Rare')::uuid AS rare \gset
\echo @@ M1 compact any of two common labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 1 AND v.value IN (:'a', :'b')) ORDER BY id LIMIT 101;
\echo @@ M2 compact rare label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 1 AND v.value = :'rare') ORDER BY id LIMIT 101;
\echo @@ M3 compact all of two labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 1 AND v.value = :'a')
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 1 AND v.value = :'b') ORDER BY id LIMIT 101;
\echo @@ M4 compact none of a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 1 AND v.value = :'a') ORDER BY id LIMIT 101;
\echo @@ M5 compact count per label
EXPLAIN (ANALYZE, COSTS OFF) SELECT value, count(*) FROM item_values_compact WHERE list_id = :'l' AND field = 1 GROUP BY value;
\echo @@ M6 compact items referencing one contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 2 AND v.value = :'contact') ORDER BY id LIMIT 101;
\echo @@ M7 compact label and contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 1 AND v.value = :'a')
  AND EXISTS (SELECT 1 FROM item_values_compact v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 2 AND v.value = :'contact') ORDER BY id LIMIT 101;
\echo @@ M8 compact count with a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM item_values_compact WHERE list_id = :'l' AND field = 1 AND value = :'a';
