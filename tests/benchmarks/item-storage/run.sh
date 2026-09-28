#!/usr/bin/env bash
# Item and permission storage benchmark: see README.md and docs/item-and-permission-storage.md.
# Needs psql, pgbench and python3. Connection through the usual PG* variables (PGHOST, PGPORT, PGUSER, PGDATABASE).
# Every result line starts with "@@"; they are collected in $OUT/results.txt.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${OUT:-$HERE/out}
PGBENCH=${PGBENCH:-pgbench}
RUN_SECONDS=${RUN_SECONDS:-8}
mkdir -p "$OUT"

psql_run() { psql -X -v ON_ERROR_STOP=1 "$@"; }

# Runs a script twice (the first warms the cache) and prints "@@ pg <prefix><label>: <ms>" per EXPLAIN ANALYZE.
explain() {
  local file=$1 prefix=$2; shift 2
  psql_run -q -f "$HERE/pg/$file" "$@" > /dev/null
  psql_run -q -f "$HERE/pg/$file" "$@" | awk -v p="$prefix" '/^@@ /{sub(/^@@ /, ""); label=$0} /Execution Time/{printf "@@ pg %s%s: %s ms\n", p, label, $3}'
}

pgbench_run() {
  local name=$1 script=$2 clients=$3 out
  out=$($PGBENCH -n -M simple -f "$HERE/pgbench/$script" -c "$clients" -j $(( clients < 4 ? clients : 4 )) -T "$RUN_SECONDS" 2>&1)
  printf '@@ pgbench %s c=%s: %s tps, latency %s ms, failed %s\n' "$name" "$clients" \
    "$(grep -oP 'tps = \K[0-9.]+' <<<"$out")" "$(grep -oP 'latency average = \K[0-9.]+' <<<"$out")" \
    "$(grep -oP 'number of failed transactions: \K[0-9]+' <<<"$out")"
}

main() {
echo "== data (1.6M items)"; psql_run -q -f "$HERE/pg/01_data.sql"

echo "== today (ADR-0011 as implemented)"
for n in 1 5; do explain 02_today.sql "u$n " -v user="u$n" -v n="$n"; done
for c in 1 4 16; do pgbench_run today today.sql "$c"; done

echo "== minimal fix (partial index for the unique-scope preload)"
explain 03_minimal_fix.sql ""
for c in 1 4 16; do pgbench_run minimal-fix today.sql "$c"; done

echo "== option A (scope ACL by principal) and C (per-user access)"
psql_run -q -f "$HERE/pg/04_option_a.sql" | sed -n '/@@ sizes/,$p'
for n in 1 5; do explain 05_option_a_queries.sql "u$n " -v user="u$n" -v n="$n"; done
for c in 1 4 16; do pgbench_run option-a option_a.sql "$c"; done

echo "== fields (slots, pivot)"
psql_run -q -f "$HERE/pg/06_fields.sql" | sed -n '/@@ sizes/,$p'
explain 07_field_queries.sql ""
python3 - "$OUT/mytasks_today.sql" <<'PY'
import hashlib, sys
g = lambda s: (lambda h: f'{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}')(hashlib.md5(s.encode()).hexdigest())
me = g('u17')
with open(sys.argv[1], 'w') as f:
    f.write('SET search_path = bench;\n')
    for l in range(1, 51):
        L = g(f'list-tasks{l}')
        f.write(f"SELECT EXISTS (SELECT 1 FROM items WHERE tenant_id = md5('tenant')::uuid AND list_id = '{L}' AND has_unique_permissions);\n")
        f.write(f"SELECT * FROM items WHERE tenant_id = md5('tenant')::uuid AND list_id = '{L}' AND deleted_at IS NULL "
                f"AND fields @> '{{\"assignedTo\":[\"{me}\"]}}' AND NOT fields @> '{{\"status\":\"Completed\"}}' ORDER BY fields->>'dueDate', id LIMIT 1000;\n")
PY
psql_run -q -o /dev/null -f "$OUT/mytasks_today.sql"
best=
for i in 1 2 3; do
  s=$(date +%s%N); psql_run -q -o /dev/null -f "$OUT/mytasks_today.sql"; e=$(date +%s%N)
  ms=$(( (e - s) / 1000000 )); best=$(( ${best:-$ms} < ms ? ${best:-$ms} : ms ))
done
echo "@@ pg T1 today loop over 50 task lists (preload 1 + JSON query each, psql over localhost): $best ms"

echo "== writes (500k CRM rows per layout, 8 clients)"
psql_run -q -f "$HERE/pg/08_write_tables.sql" 2>/dev/null
# Interleaved rounds, so checkpoints and autovacuum hit every layout alike; report the median per layout.
for round in 1 2 3; do
  for s in upd_json upd_slots upd_pivot ins_json ins_slots ins_pivot; do pgbench_run "$s round $round" "$s.sql" 8; done
done

echo "== fan-out"
psql_run -f "$HERE/pg/09_fanout.sql" | awk '/^@@ /{sub(/^@@ /, ""); label=$0} /^UPDATE/{u=1; next} /^Time:/{if (u) printf "@@ pg %s: %s ms\n", label, $2; u=0}'

echo "== SQLite (same rows)"
for q in \
  "items:SELECT upper(id::text), upper(tenant_id::text), upper(list_id::text), upper(content_type_id::text), upper(parent_id::text), is_folder::int, has_unique_permissions::int, upper(scope_id::text), title, fields::text, to_char(created_at,'YYYY-MM-DD HH24:MI:SS+00:00'), upper(created_by::text), to_char(updated_at,'YYYY-MM-DD HH24:MI:SS+00:00'), upper(updated_by::text), NULL, NULL, version, upper(scope2::text), s_text1, s_num1, s_date1::text FROM bench.items" \
  "permission_grants:SELECT upper(id::text), upper(tenant_id::text), upper(list_id::text), upper(object_id::text), principal_type, upper(principal_id::text), level FROM bench.permission_grants" \
  "acl:SELECT upper(tenant_id::text), upper(list_id::text), upper(scope_id::text), upper(principal_id::text), level FROM bench.acl" \
  "group_members:SELECT upper(group_id::text), upper(user_id::text) FROM bench.group_members" \
  "item_values:SELECT upper(tenant_id::text), upper(list_id::text), upper(item_id::text), field, ordinal, v_text, v_num, v_date::text, upper(v_guid::text) FROM bench.item_values"; do
  psql_run -q -c "\\copy (${q#*:}) TO '$OUT/${q%%:*}.csv' WITH (FORMAT csv)"
done
python3 "$HERE/sqlite/load.py" "$OUT"
rm -f "$OUT"/*.csv
python3 "$HERE/sqlite/bench.py" "$OUT"
}

main 2>&1 | tee "$OUT/log.txt"
grep '^@@' "$OUT/log.txt" > "$OUT/results.txt"
echo "== done: $OUT/results.txt"
