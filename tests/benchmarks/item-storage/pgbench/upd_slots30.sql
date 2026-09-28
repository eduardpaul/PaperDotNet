\set k random(1, 250000)
UPDATE multi.w_slots30 SET fields = fields || jsonb_build_object('a', :k), updated_at = now(), version = version + 1, t1 = 'x' || :k, n1 = :k WHERE id = md5('w' || (:k * 2 - 1))::uuid;
