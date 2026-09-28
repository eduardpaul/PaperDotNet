\set n random(1, 500000)
UPDATE bench.w_slots SET fields = fields || jsonb_build_object('stage', 'Proposal', 'amount', :n % 90000), s_text1 = 'Proposal', s_num1 = :n % 90000, updated_at = now(), version = version + 1 WHERE id = md5('deal' || :n)::uuid;
