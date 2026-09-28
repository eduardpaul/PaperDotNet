-- Field query shapes on the CRM list (500k deals) and 50 task lists.
SET search_path = bench;
SELECT md5('list-crm')::uuid AS crm, md5('tenant')::uuid AS t, md5('u17')::uuid AS me \gset
SELECT '{' || string_agg(DISTINCT list_id::text, ',') || '}' AS tasklists FROM items WHERE content_type_id = md5('ct-task')::uuid \gset
-- C1 CRM view: stage = Negotiation order by closeDate, first 100
\echo @@ C1 JSON (today)
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'crm' AND deleted_at IS NULL
  AND fields @> '{"stage":"Negotiation"}' ORDER BY fields->>'closeDate', id LIMIT 101;
\echo @@ C1 slots
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'crm' AND deleted_at IS NULL
  AND s_text1 = 'Negotiation' AND s_date1 IS NOT NULL ORDER BY s_date1, id LIMIT 101;
\echo @@ C1 pivot
EXPLAIN (ANALYZE, COSTS OFF) SELECT i.* FROM item_values d JOIN items i ON i.id = d.item_id
  WHERE d.list_id = :'crm' AND d.field = 'closeDate' AND d.v_date IS NOT NULL AND i.tenant_id = :'t' AND i.deleted_at IS NULL
  AND EXISTS (SELECT 1 FROM item_values s WHERE s.item_id = i.id AND s.field = 'stage' AND s.list_id = :'crm' AND s.v_text = 'Negotiation')
  ORDER BY d.v_date, d.item_id LIMIT 101;

-- C2 CRM range: amount 50000..51000 and closeDate in Q4 2024, count
\echo @@ C2 JSON (today)
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM items WHERE tenant_id = :'t' AND list_id = :'crm' AND deleted_at IS NULL
  AND (fields->>'amount')::numeric BETWEEN 50000 AND 51000 AND fields->>'closeDate' BETWEEN '2024-10-01' AND '2024-12-31';
\echo @@ C2 slots
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM items WHERE tenant_id = :'t' AND list_id = :'crm' AND deleted_at IS NULL
  AND s_num1 BETWEEN 50000 AND 51000 AND s_date1 BETWEEN '2024-10-01' AND '2024-12-31';
\echo @@ C2 pivot
EXPLAIN (ANALYZE, COSTS OFF) SELECT count(*) FROM item_values a JOIN item_values d ON d.item_id = a.item_id AND d.field = 'closeDate' AND d.list_id = :'crm'
  WHERE a.list_id = :'crm' AND a.field = 'amount' AND a.v_num BETWEEN 50000 AND 51000 AND d.v_date BETWEEN '2024-10-01' AND '2024-12-31';

-- C3 board: count per stage
\echo @@ C3 JSON (today)
EXPLAIN (ANALYZE, COSTS OFF) SELECT fields->>'stage', count(*) FROM items WHERE tenant_id = :'t' AND list_id = :'crm' AND deleted_at IS NULL GROUP BY 1;
\echo @@ C3 slots
EXPLAIN (ANALYZE, COSTS OFF) SELECT s_text1, count(*) FROM items WHERE list_id = :'crm' AND s_text1 IS NOT NULL GROUP BY 1;
\echo @@ C3 pivot
EXPLAIN (ANALYZE, COSTS OFF) SELECT v_text, count(*) FROM item_values WHERE list_id = :'crm' AND field = 'stage' AND v_text IS NOT NULL GROUP BY 1;

-- C4 reverse lookup: deals of one account
\echo @@ C4 JSON (today, GIN)
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = :'crm' AND deleted_at IS NULL
  AND fields @> jsonb_build_object('account', md5('acc42')::uuid::text) ORDER BY id LIMIT 101;
\echo @@ C4 pivot
EXPLAIN (ANALYZE, COSTS OFF) SELECT i.* FROM item_values v JOIN items i ON i.id = v.item_id WHERE v.field = 'account' AND v.v_guid = md5('acc42')::uuid AND v.list_id = :'crm' ORDER BY i.id LIMIT 101;

-- T1 my open tasks across 50 task lists by due date
\echo @@ T1 JSON one list (today runs this per list, x50, plus the per-list access preload)
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = md5('list-tasks7')::uuid AND deleted_at IS NULL
  AND fields @> jsonb_build_object('assignedTo', jsonb_build_array(:'me'::text)) AND NOT fields @> '{"status":"Completed"}'
  ORDER BY fields->>'dueDate', id LIMIT 1000;
\echo @@ T1 JSON cross-list single query
EXPLAIN (ANALYZE, COSTS OFF) SELECT * FROM items WHERE tenant_id = :'t' AND list_id = ANY(:'tasklists'::uuid[]) AND deleted_at IS NULL
  AND fields @> jsonb_build_object('assignedTo', jsonb_build_array(:'me'::text)) AND NOT fields @> '{"status":"Completed"}'
  ORDER BY fields->>'dueDate', id LIMIT 100;
\echo @@ T1 pivot (assignedTo) + slots (status, dueDate), cross-list single query
EXPLAIN (ANALYZE, COSTS OFF) SELECT i.* FROM item_values v JOIN items i ON i.id = v.item_id
  WHERE v.field = 'assignedTo' AND v.v_guid = :'me' AND v.list_id = ANY(:'tasklists'::uuid[]) AND i.deleted_at IS NULL AND i.s_text1 <> 'Completed'
  ORDER BY i.s_date1, i.id LIMIT 100;
