\set k random(1, 250000)
UPDATE multi.w_base SET fields = fields || jsonb_build_object('a', :k), updated_at = now(), version = version + 1 WHERE id = md5('w' || (:k * 2 - 0))::uuid;
