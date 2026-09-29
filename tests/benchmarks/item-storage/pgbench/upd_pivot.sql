\set n random(1, 500000)
BEGIN;
UPDATE bench.w_pivot SET fields = fields || jsonb_build_object('stage', 'Proposal', 'amount', :n % 90000), updated_at = now(), version = version + 1 WHERE id = md5('deal' || :n)::uuid;
UPDATE bench.w_pivot_values SET v_text = 'Proposal' WHERE item_id = md5('deal' || :n)::uuid AND field = 'stage' AND ordinal = 0;
UPDATE bench.w_pivot_values SET v_num = :n % 90000 WHERE item_id = md5('deal' || :n)::uuid AND field = 'amount' AND ordinal = 0;
COMMIT;
