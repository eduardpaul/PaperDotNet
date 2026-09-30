#!/usr/bin/env bash
# Publishes the core as a Native AOT binary, runs it on a fresh SQLite database, exercises the API (token, lists,
# items with OData filters, events through the outbox to the audit log) and checks the memory budget (ADR-0039):
# resident memory under IDLE_BUDGET_MB after start and under LOAD_BUDGET_MB during a burst of parallel writes/reads.
#
#   eng/aot-smoke.sh              publish, then test
#   eng/aot-smoke.sh --no-publish test artifacts/aot/paperdotnet as it is
set -euo pipefail
cd "$(dirname "$0")/.."

IDLE_BUDGET_MB=${IDLE_BUDGET_MB:-100}
LOAD_BUDGET_MB=${LOAD_BUDGET_MB:-300}
PORT=${PORT:-5099}
RID=${RID:-linux-x64}
BASE="http://127.0.0.1:$PORT"
OUT=artifacts/aot

if [[ "${1:-}" != "--no-publish" ]]; then
  dotnet publish src/PaperDotNet.Core.Host -c Release -r "$RID" -o "$OUT"
fi
echo "Binary: $(du -h "$OUT/paperdotnet" | cut -f1)"

DATA=$(mktemp -d)
LOG="$DATA/host.log"
PASSWORD='Smoke-Test-Pass-1'
Storage__DataPath="$DATA" ASPNETCORE_URLS="$BASE" Logging__LogLevel__Default=Warning \
  Bootstrap__AdminUserName=admin Bootstrap__AdminPassword="$PASSWORD" "$OUT/paperdotnet" >"$LOG" 2>&1 &
PID=$!
cleanup() { kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; rm -rf "$DATA"; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; echo "--- host log" >&2; tail -50 "$LOG" >&2; exit 1; }
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1], {'d': d}))" "$1"; }
rss_mb() { echo $(( $(awk '/VmRSS/{print $2}' "/proc/$PID/status") / 1024 )); }

for _ in $(seq 1 50); do curl -sf "$BASE/health" >/dev/null && break; sleep 0.2; done
curl -sf "$BASE/health" >/dev/null || fail "the host did not start"
"$OUT/paperdotnet" healthcheck "$BASE" || fail "healthcheck command"

TOKEN=$(curl -sf -X POST "$BASE/connect/token" -d grant_type=password -d username=admin --data-urlencode "password=$PASSWORD" | json 'd["access_token"]') || fail "token"
AUTH=(-H "Authorization: Bearer $TOKEN")
sleep 2; IDLE=$(rss_mb)
curl -sf "$BASE/openapi/v1.json" | json '"/v1.0/lists" in d["paths"]' | grep -q True || fail "OpenAPI document"

LIST=$(curl -sf "${AUTH[@]}" -H 'Content-Type: application/json' "$BASE/v1.0/lists" \
  -d '{"name":"Invoices","fields":[{"name":"amount","type":"number"},{"name":"paid","type":"boolean"},{"name":"due","type":"dateTime"}]}' | json 'd["id"]') || fail "create list"
for n in 1 2 3 4 5; do
  curl -sf "${AUTH[@]}" -H 'Content-Type: application/json' "$BASE/v1.0/lists/$LIST/items" -o /dev/null \
    -d "{\"fields\":{\"title\":\"Invoice $n\",\"amount\":$((n * 10)),\"paid\":$([[ $n -gt 3 ]] && echo true || echo false),\"due\":\"2026-10-0${n}T00:00:00Z\"}}" || fail "create item $n"
done

TITLES=$(curl -sf "${AUTH[@]}" -G "$BASE/v1.0/lists/$LIST/items" --data-urlencode '$filter=fields/amount gt 15 and fields/paid eq false' \
  --data-urlencode '$orderby=fields/amount desc' --data-urlencode '$count=true' | json '",".join(i["fields"]["title"] for i in d["value"]) + "|" + str(d["@odata.count"])') || fail "query items"
[[ "$TITLES" == "Invoice 3,Invoice 2|2" ]] || fail "unexpected query result: $TITLES"

AUDITED=0
for _ in $(seq 1 50); do
  AUDITED=$(curl -sf "${AUTH[@]}" "$BASE/v1.0/audit?\$top=100" | json 'len(d["value"])')
  [[ "$AUDITED" -ge 6 ]] && break; sleep 0.2
done
[[ "$AUDITED" -ge 6 ]] || fail "events did not reach the audit log ($AUDITED of 6)"

# Workflows (ADR-0036) with a Jint script step: an added invoice over 25 creates a task through a script.
curl -sf "${AUTH[@]}" -H 'Content-Type: application/json' "$BASE/v1.0/lists" -d '{"name":"Tasks"}' -o /dev/null || fail "create task list"
WORKFLOW=$(curl -sf "${AUTH[@]}" -H 'Content-Type: application/json' "$BASE/v1.0/workflows" -d '{"name":"Follow up","definition":{
  "trigger":{"type":"itemAdded","list":"Invoices"},"condition":"fields/amount gt 25",
  "flow":{"start":"script","nodes":{
    "script":{"activity":"script","inputs":{"code":"const all = await items.query(\"Invoices\", { filter: \"fields/amount gt 25\" }); await items.create(\"Tasks\", { title: `Follow up ${item.title} (${all.length})` }); return all.length;"},"next":{"done":"mark"}},
    "mark":{"activity":"item.update","inputs":{"fields":{"paid":true}}}}}}}' | json 'd["id"]') || fail "create workflow"
curl -sf "${AUTH[@]}" -H 'Content-Type: application/json' "$BASE/v1.0/lists/$LIST/items" -o /dev/null -d '{"fields":{"title":"Invoice 6","amount":60}}' || fail "create item 6"
RUN=""
for _ in $(seq 1 50); do
  RUN=$(curl -sf "${AUTH[@]}" "$BASE/v1.0/workflows/$WORKFLOW/runs" | json '",".join(r["status"] + ":" + str(r["outputs"].get("script", {}).get("result")) for r in d["value"])')
  [[ "$RUN" == completed:* ]] && break; sleep 0.2
done
[[ "$RUN" == "completed:4" ]] || fail "workflow run: $RUN"

# Burst: parallel writes (each also queues an event and a workflow check) and filtered reads.
seq 1 2000 | xargs -P 16 -I{} curl -sf -o /dev/null "${AUTH[@]}" -H 'Content-Type: application/json' "$BASE/v1.0/lists/$LIST/items" \
  -d '{"fields":{"title":"Load {}","amount":{}}}' || fail "write burst"
PEAK=$(rss_mb)
seq 1 500 | xargs -P 16 -I{} curl -sf -o /dev/null "${AUTH[@]}" -G "$BASE/v1.0/lists/$LIST/items" \
  --data-urlencode '$filter=fields/amount gt {}' --data-urlencode '$top=50' || fail "read burst"
PEAK=$(( $(rss_mb) > PEAK ? $(rss_mb) : PEAK ))
sleep 3
AFTER=$(rss_mb)
PEAK=$(( AFTER > PEAK ? AFTER : PEAK ))

echo "Memory: idle ${IDLE} MB (budget ${IDLE_BUDGET_MB}), under load ${PEAK} MB (budget ${LOAD_BUDGET_MB})"
[[ "$IDLE" -lt "$IDLE_BUDGET_MB" ]] || fail "idle memory ${IDLE} MB over budget"
[[ "$PEAK" -lt "$LOAD_BUDGET_MB" ]] || fail "memory under load ${PEAK} MB over budget"
! grep -E "Unhandled exception|fail:" "$LOG" || fail "errors in the host log"
echo "AOT smoke test passed."
