SET search_path = multi;
SELECT md5('list-multi')::uuid AS l, md5('c123')::uuid::text AS contact \gset
-- M1 any of two common labels, first 100 by id
\echo @@ M1 JSON any of two common labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE list_id = :'l' AND deleted_at IS NULL
  AND (fields @> '{"labels":["Label3"]}' OR fields @> '{"labels":["Label5"]}') ORDER BY id LIMIT 101;
\echo @@ M1 values any of two common labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'labels' AND v.value IN ('Label3', 'Label5')) ORDER BY id LIMIT 101;
-- M2 any of a rare label (0.5% of items)
\echo @@ M2 JSON rare label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE list_id = :'l' AND deleted_at IS NULL AND fields @> '{"labels":["Rare"]}' ORDER BY id LIMIT 101;
\echo @@ M2 values rare label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'labels' AND v.value = 'Rare') ORDER BY id LIMIT 101;
-- M3 all of two labels
\echo @@ M3 JSON all of two labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE list_id = :'l' AND deleted_at IS NULL
  AND fields @> '{"labels":["Label3"]}' AND fields @> '{"labels":["Label5"]}' ORDER BY id LIMIT 101;
\echo @@ M3 values all of two labels
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'labels' AND v.value = 'Label3')
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'labels' AND v.value = 'Label5') ORDER BY id LIMIT 101;
-- M4 none of a label
\echo @@ M4 JSON none of a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE list_id = :'l' AND deleted_at IS NULL AND NOT fields @> '{"labels":["Label3"]}' ORDER BY id LIMIT 101;
\echo @@ M4 values none of a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'labels' AND v.value = 'Label3') ORDER BY id LIMIT 101;
-- M5 count per label (board columns, group-by folders; not possible today)
\echo @@ M5 JSON count per label
EXPLAIN (ANALYZE, COSTS OFF) SELECT l.value, count(*) FROM items i, jsonb_array_elements_text(i.fields->'labels') l WHERE i.list_id = :'l' AND i.deleted_at IS NULL GROUP BY 1;
\echo @@ M5 values count per label
EXPLAIN (ANALYZE, COSTS OFF) SELECT value, count(*) FROM item_values WHERE list_id = :'l' AND field = 'labels' GROUP BY value;
-- M6 reverse multi-lookup: items that reference one contact
\echo @@ M6 JSON items referencing one contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE list_id = :'l' AND deleted_at IS NULL AND fields @> jsonb_build_object('contacts', jsonb_build_array(:'contact')) ORDER BY id LIMIT 101;
\echo @@ M6 values items referencing one contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'contacts' AND v.value = :'contact') ORDER BY id LIMIT 101;
-- M7 a label and a contact
\echo @@ M7 JSON label and contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE list_id = :'l' AND deleted_at IS NULL
  AND fields @> '{"labels":["Label3"]}' AND fields @> jsonb_build_object('contacts', jsonb_build_array(:'contact')) ORDER BY id LIMIT 101;
\echo @@ M7 values label and contact
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items i WHERE list_id = :'l' AND deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'labels' AND v.value = 'Label3')
  AND EXISTS (SELECT 1 FROM item_values v WHERE v.item_id = i.id AND v.list_id = :'l' AND v.field = 'contacts' AND v.value = :'contact') ORDER BY id LIMIT 101;
-- M8 count items with a label (the $count of a filtered view)
\echo @@ M8 JSON count with a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM items WHERE list_id = :'l' AND deleted_at IS NULL AND fields @> '{"labels":["Label3"]}';
\echo @@ M8 values count with a label
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM item_values WHERE list_id = :'l' AND field = 'labels' AND value = 'Label3';
