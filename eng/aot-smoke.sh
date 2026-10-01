#!/usr/bin/env bash
# Publishes the server as a Native AOT binary, runs it on a fresh SQLite database, exercises the API (token, workspaces, lists,
# items with OData filters, events through the outbox to the audit log) and checks the memory budget (ADR-0039):
# resident memory under IDLE_BUDGET_MB after start and under LOAD_BUDGET_MB during a burst of parallel writes/reads.
# The build includes the Invoices sample extension, so an extension's own tables (compiled model, precompiled queries,
# embedded SQL) are checked under AOT too.
#
#   eng/aot-smoke.sh              publish, then test
#   eng/aot-smoke.sh --no-publish test artifacts/aot/paperdotnet as it is
set -euo pipefail
cd "$(dirname "$0")/.."

IDLE_BUDGET_MB=${IDLE_BUDGET_MB:-150}
LOAD_BUDGET_MB=${LOAD_BUDGET_MB:-300}
PORT=${PORT:-5099}
RID=${RID:-linux-x64}
BASE="http://127.0.0.1:$PORT"
OUT=artifacts/aot

if [[ "${1:-}" != "--no-publish" ]]; then
  dotnet publish src/PaperDotNet.Host -c Release -r "$RID" -o "$OUT" \
    -p:PaperDotNetExtensions=../../samples/PaperDotNet.Samples.Invoices/PaperDotNet.Samples.Invoices.csproj
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
JSON=(-H 'Content-Type: application/json')
curl -sf "$BASE/openapi/v1.json" | json '"/v1.0/workspaces/{workspaceId}/lists/{listId}/items" in d["paths"]' | grep -q True || fail "OpenAPI document"

# Lists in a workspace: a content type with fields, a list, items, OData queries, a folder with an item inside.
WS=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces" -d '{"name":"Finance"}' | json 'd["id"]') || fail "create workspace"
INVOICE=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/contentTypes" \
  -d '{"name":"Invoice","fields":[{"name":"amount","type":"number","indexed":true},{"name":"paid","type":"boolean"},{"name":"due","type":"dateTime"}]}' | json 'd["id"]') || fail "create content type"
LIST=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists" -d "{\"name\":\"Invoices\",\"versioning\":\"major\",\"contentTypeIds\":[\"$INVOICE\"]}" | json 'd["id"]') || fail "create list"
ITEMS="$BASE/v1.0/workspaces/$WS/lists/$LIST/items"
for n in 1 2 3 4 5; do
  curl -sf "${AUTH[@]}" "${JSON[@]}" "$ITEMS" -o /dev/null \
    -d "{\"fields\":{\"title\":\"Invoice $n\",\"amount\":$((n * 10)),\"paid\":$([[ $n -gt 3 ]] && echo true || echo false),\"due\":\"2026-10-0${n}T00:00:00Z\"}}" || fail "create item $n"
done

TITLES=$(curl -sf "${AUTH[@]}" -G "$ITEMS" --data-urlencode '$filter=fields/amount gt 15 and fields/paid eq false' \
  --data-urlencode '$orderby=fields/amount desc' --data-urlencode '$count=true' | json '",".join(i["fields"]["title"] for i in d["value"]) + "|" + str(d["@odata.count"])') || fail "query items"
[[ "$TITLES" == "Invoice 3,Invoice 2|2" ]] || fail "unexpected query result: $TITLES"
VIEW=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists/$LIST/views" -d '{"name":"Unpaid","filter":"fields/paid eq false","orderBy":"fields/amount desc","columns":["title"]}' | json 'd["id"]') || fail "create view"
[[ $(curl -sf "${AUTH[@]}" "$ITEMS?viewId=$VIEW" | json '",".join(i["fields"]["title"] for i in d["value"])') == "Invoice 3,Invoice 2,Invoice 1" ]] || fail "items through a view"
[[ $(curl -sf "${AUTH[@]}" "$ITEMS/counts?field=paid" | json '",".join(str(c["value"]) + ":" + str(c["count"]) for c in d["value"])') == "false:3,true:2" ]] || fail "item counts"
FOLDER=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$ITEMS" -d '{"isFolder":true,"fields":{"title":"2026"}}' | json 'd["id"]') || fail "create folder"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$ITEMS" -o /dev/null -d "{\"parentId\":\"$FOLDER\",\"fields\":{\"title\":\"Filed\",\"amount\":1}}" || fail "create item in folder"
[[ $(curl -sf "${AUTH[@]}" "$ITEMS/$FOLDER/children" | json 'd["value"][0]["fields"]["title"]') == Filed ]] || fail "folder children"

# Versions and the recycle bin: a change adds a version; a deleted item can be restored.
read -r ITEM ETAG < <(curl -sf "${AUTH[@]}" "${JSON[@]}" "$ITEMS" -d '{"fields":{"title":"Versioned","amount":1}}' | json 'd["id"] + " " + d["@odata.etag"]') || fail "create versioned item"
curl -sf -X PATCH "${AUTH[@]}" "${JSON[@]}" -H "If-Match: $ETAG" "$ITEMS/$ITEM" -d '{"fields":{"amount":2}}' -o /dev/null || fail "update versioned item"
VERSIONS=$(curl -s "${AUTH[@]}" "$ITEMS/$ITEM/versions")
[[ $(json '",".join(str(v["number"]) for v in d["value"])' <<<"$VERSIONS") == "2,1" ]] || fail "item versions: $VERSIONS"
curl -sf -X DELETE "${AUTH[@]}" -H 'If-Match: "2"' "$ITEMS/$ITEM" || fail "delete item"
[[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/workspaces/$WS/lists/$LIST/recycleBin" | json 'len(d["value"])') == 1 ]] || fail "recycle bin"
curl -sf -X POST "${AUTH[@]}" "$BASE/v1.0/workspaces/$WS/lists/$LIST/recycleBin/$ITEM/restore" -o /dev/null || fail "restore item"

# Bulk update as an operation: the item matching the filter is changed in the background.
OPERATION=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$ITEMS/bulkUpdate" -d "{\"filter\":\"fields/title eq 'Versioned'\",\"fields\":{\"amount\":3}}" | json 'd["id"]') || fail "start bulk update"
BULK=""
for _ in $(seq 1 50); do
  BULK=$(curl -sf "${AUTH[@]}" "$BASE/v1.0/operations/$OPERATION" | json 'd["status"] + ":" + str((d.get("result") or {}).get("updated"))')
  [[ "$BULK" == succeeded:* || "$BULK" == failed:* ]] && break; sleep 0.2
done
[[ "$BULK" == "succeeded:1" ]] || fail "bulk update: $BULK"
DELTA=$(curl -sf "${AUTH[@]}" "$ITEMS/delta" | json 'str(len(d["value"])) + " " + d["@odata.deltaLink"]') || fail "delta sync"
[[ ${DELTA%% *} -ge 7 ]] || fail "delta items: ${DELTA%% *}"
curl -sf "${AUTH[@]}" "${DELTA#* }" -o /dev/null || fail "delta changes"
CONTACTS=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists" -d '{"name":"Clients","templateKey":"contacts"}' | json 'd["id"]') || fail "list from a template"
[[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/workspaces/$WS/lists/$CONTACTS/views" | json 'd[0]["name"]') == "All contacts" ]] || fail "template views"
# An extension with its own table (the Invoices sample, EXT-07).
[[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/extensions" | json '",".join(e["id"] + ":" + str(e["enabled"]) for e in d)') == "samples.invoices:False" ]] || fail "extension catalog"
curl -sf -X POST "${AUTH[@]}" "$BASE/v1.0/extensions/samples.invoices/enable" -o /dev/null || fail "enable extension"
BILLS=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists" -d '{"name":"Bills","templateKey":"samples.invoices.invoices"}' | json 'd["id"]') || fail "list from an extension template"
read -r BILL STATUS < <(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists/$BILLS/items" -d '{"fields":{"title":"Big","amount":5000,"iban":"DE89 3704 0044 0532 0130 00"}}' | json 'd["id"] + " " + d["fields"]["status"]') || fail "create invoice"
[[ "$STATUS" == pendingApproval ]] || fail "extension item mutator: $STATUS"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/ext/samples.invoices/workspaces/$WS/lists/$BILLS/items/$BILL/approve" -d '{"comment":"ok"}' -o /dev/null || fail "approve invoice"
[[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/ext/samples.invoices/approvals" | json 'd[0]["itemId"] + " " + d[0]["comment"]') == "$BILL ok" ]] || fail "extension table"
# Notifications: inbox, settings, follows.
curl -sf -X PUT "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/me/notificationSettings" -d '{"digestHour":6}' -o /dev/null || fail "notification settings"
curl -sf -X POST "${AUTH[@]}" "$BASE/v1.0/me/notificationSettings/test" -o /dev/null || fail "test notification"
[[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/me/notifications/unreadCount" | json 'd["count"]') == 1 ]] || fail "notification inbox"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/me/subscriptions" -d "{\"workspaceId\":\"$WS\",\"listId\":\"$LIST\",\"frequency\":\"daily\"}" -o /dev/null || fail "follow a list"
# Collaboration and Notes.
BILL_URL="$BASE/v1.0/workspaces/$WS/lists/$BILLS/items/$BILL"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BILL_URL/comments" -d '{"text":"Looks right"}' -o /dev/null || fail "comment"
[[ $(curl -sf "${AUTH[@]}" "$BILL_URL/comments" | json 'd["value"][0]["text"]') == "Looks right" ]] || fail "comments"
for _ in $(seq 1 50); do [[ $(curl -sf "${AUTH[@]}" "$BILL_URL/activity" | json 'len(d["value"])') -ge 2 ]] && break; sleep 0.2; done
[[ $(curl -sf "${AUTH[@]}" "$BILL_URL/activity" | json '",".join(sorted(set(a["kind"] for a in d["value"])))') == *commented* ]] || fail "item activity"
WIKI=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists" -d '{"name":"Wiki","templateKey":"notes"}' | json 'd["id"]') || fail "notes list"
HOME_NOTE=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists/$WIKI/items" -d '{"fields":{"title":"Home","body":"See [[Plans]]."}}' | json 'd["id"]') || fail "create note"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists/$WIKI/items" -d '{"fields":{"title":"Plans","body":"Back [[Home]]."}}' -o /dev/null || fail "create note"
for _ in $(seq 1 50); do [[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/workspaces/$WS/lists/$WIKI/items/$HOME_NOTE/noteLinks" | json '"note" in d["value"][0]' 2>/dev/null) == True ]] && break; sleep 0.2; done
[[ $(curl -sf "${AUTH[@]}" "$BASE/v1.0/workspaces/$WS/lists/$WIKI/items/$HOME_NOTE/noteLinks" | json 'd["value"][0]["note"]["title"]') == Plans ]] || fail "note links"
# Tasks: checklists and repeating tasks (Ical.Net under AOT).
TODO=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists" -d '{"name":"Chores","templateKey":"tasks"}' | json 'd["id"]') || fail "task list"
read -r CHORE CHORE_ETAG < <(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists/$TODO/items" -d '{"fields":{"title":"Water plants","dueDate":"2026-10-05"}}' | json 'd["id"] + " " + d["@odata.etag"]') || fail "create task"
CHORE_URL="$BASE/v1.0/workspaces/$WS/lists/$TODO/items/$CHORE"
curl -sf -X PUT "${AUTH[@]}" "${JSON[@]}" "$CHORE_URL/checklist" -d '[{"text":"Balcony","done":true}]' -o /dev/null || fail "checklist"
[[ $(curl -sf -X PUT "${AUTH[@]}" "${JSON[@]}" "$CHORE_URL/recurrence" -d '{"rule":"FREQ=WEEKLY;BYDAY=MO"}' | json 'd["nextDueDate"]') == 2026-10-12 ]] || fail "task recurrence"
curl -sf -X PATCH "${AUTH[@]}" "${JSON[@]}" -H "If-Match: $CHORE_ETAG" "$CHORE_URL" -d '{"fields":{"status":"completed"}}' -o /dev/null || fail "complete task"
for _ in $(seq 1 50); do [[ $(curl -sf "${AUTH[@]}" -G "$BASE/v1.0/workspaces/$WS/lists/$TODO/items" --data-urlencode '$filter=fields/dueDate eq 2026-10-12' | json 'len(d["value"])') == 1 ]] && break; sleep 0.2; done
[[ $(curl -sf "${AUTH[@]}" -G "$BASE/v1.0/workspaces/$WS/lists/$TODO/items" --data-urlencode '$filter=fields/dueDate eq 2026-10-12' | json 'len(d["value"])') == 1 ]] || fail "next occurrence of a repeating task"

AUDITED=0
for _ in $(seq 1 50); do
  AUDITED=$(curl -sf "${AUTH[@]}" "$BASE/v1.0/audit?\$top=100" | json 'len(d["value"])')
  [[ "$AUDITED" -ge 8 ]] && break; sleep 0.2
done
[[ "$AUDITED" -ge 8 ]] || fail "events did not reach the audit log ($AUDITED of 8)"

# Workflows (ADR-0036) with a Jint script step: an added invoice over 25 creates a task through a script.
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/lists" -d '{"name":"Tasks"}' -o /dev/null || fail "create task list"
WORKFLOW=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WS/workflows" -d '{"name":"Follow up","definition":{
  "trigger":{"type":"itemAdded","list":"Invoices"},"condition":"fields/amount gt 25",
  "flow":{"start":"script","nodes":{
    "script":{"activity":"script","inputs":{"code":"const all = await items.query(\"Invoices\", { filter: \"fields/amount gt 25\" }); await items.create(\"Tasks\", { title: `Follow up ${item.title} (${all.length})` }); return all.length;"},"next":{"done":"mark"}},
    "mark":{"activity":"item.update","inputs":{"fields":{"paid":true}}}}}}}' | json 'd["id"]') || fail "create workflow"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$ITEMS" -o /dev/null -d '{"fields":{"title":"Invoice 6","amount":60}}' || fail "create item 6"
RUN=""
for _ in $(seq 1 50); do
  RUN=$(curl -sf "${AUTH[@]}" "$BASE/v1.0/workspaces/$WS/workflows/$WORKFLOW/runs" | json '",".join(r["status"] + ":" + str(r["outputs"].get("script", {}).get("result")) for r in d["value"])')
  [[ "$RUN" == completed:* ]] && break; sleep 0.2
done
[[ "$RUN" == "completed:4" ]] || fail "workflow run: $RUN"

# Identity: a member in a group inside a group gets a role's scope; preferences; an API token limited to one scope.
MEMBER=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/users" -d '{"userName":"member","password":"member-password-1"}' | json 'd["id"]') || fail "create user"
OUTER=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/groups" -d '{"name":"Outer"}' | json 'd["id"]') || fail "create group"
INNER=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/groups" -d '{"name":"Inner"}' | json 'd["id"]') || fail "create group"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/groups/$INNER/members" -d "{\"userId\":\"$MEMBER\"}" || fail "add member"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/groups/$OUTER/groups" -d "{\"groupId\":\"$INNER\"}" || fail "nest group"
ROLE=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/roles" -d '{"name":"Readers","scopes":["role.read"]}' | json 'd["id"]') || fail "create role"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/roles/$ROLE/assignments" -d "{\"principalId\":\"$OUTER\",\"principalType\":\"group\"}" -o /dev/null || fail "assign role"
MEMBER_TOKEN=$(curl -sf -X POST "$BASE/connect/token" -d grant_type=password -d username=member -d password=member-password-1 | json 'd["access_token"]') || fail "member token"
curl -sf -H "Authorization: Bearer $MEMBER_TOKEN" "$BASE/v1.0/roles" -o /dev/null || fail "role through nested groups"
curl -sf -X PATCH -H "Authorization: Bearer $MEMBER_TOKEN" "${JSON[@]}" "$BASE/v1.0/me/preferences" -d '{"timeZone":"Europe/Berlin"}' -o /dev/null || fail "preferences"
SECRET=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/me/apiTokens" -d '{"name":"smoke","scopes":["list.read"]}' | json 'd["secret"]') || fail "API token"
curl -sf -H "Authorization: Bearer $SECRET" "$ITEMS" -o /dev/null || fail "API token read"
[[ $(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $SECRET" "$BASE/v1.0/users") == 403 ]] || fail "API token scope"

# Workspaces: the member sees a workspace they were added to, with its access level.
WORKSPACE=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces" -d '{"name":"Team"}' | json 'd["id"]') || fail "create workspace"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WORKSPACE/members" -d "{\"userId\":\"$MEMBER\",\"role\":\"visitor\"}" || fail "add workspace member"
[[ $(curl -sf -H "Authorization: Bearer $MEMBER_TOKEN" "$BASE/v1.0/workspaces" | json 'd["value"][0]["access"]') == read ]] || fail "workspace access"
NOTES=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WORKSPACE/lists" -d '{"name":"Notes"}' | json 'd["id"]') || fail "create team list"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$BASE/v1.0/workspaces/$WORKSPACE/lists/$NOTES/items" -o /dev/null -d '{"fields":{"title":"Hello"}}' || fail "create team item"
[[ $(curl -sf -H "Authorization: Bearer $MEMBER_TOKEN" "$BASE/v1.0/workspaces/$WORKSPACE/lists/$NOTES/items" | json 'len(d["value"])') == 1 ]] || fail "visitor reads items"
[[ $(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $MEMBER_TOKEN" "${JSON[@]}" "$BASE/v1.0/workspaces/$WORKSPACE/lists/$NOTES/items" -d '{"fields":{"title":"No"}}') == 403 ]] || fail "visitor cannot write"
[[ $(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $MEMBER_TOKEN" "$ITEMS") == 404 ]] || fail "other workspace hidden"

# Item permissions (ADR-0035): a folder with unique permissions hides its contents from the visitor until reset.
TEAM_ITEMS="$BASE/v1.0/workspaces/$WORKSPACE/lists/$NOTES/items"
PRIVATE=$(curl -sf "${AUTH[@]}" "${JSON[@]}" "$TEAM_ITEMS" -d '{"isFolder":true,"fields":{"title":"Private"}}' | json 'd["id"]') || fail "create private folder"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$TEAM_ITEMS" -o /dev/null -d "{\"parentId\":\"$PRIVATE\",\"fields\":{\"title\":\"Secret\"}}" || fail "create secret item"
visible() { curl -sf -H "Authorization: Bearer $MEMBER_TOKEN" "$TEAM_ITEMS" | json 'len(d["value"])'; }
[[ $(visible) == 3 ]] || fail "visitor sees the folder"
curl -sf "${AUTH[@]}" "${JSON[@]}" "$TEAM_ITEMS/$PRIVATE/permissions/breakInheritance" -d '{"copyGrants":false}' -o /dev/null || fail "break inheritance"
[[ $(visible) == 1 ]] || fail "unique permissions hide the folder"
curl -sf -X POST "${AUTH[@]}" "$TEAM_ITEMS/$PRIVATE/permissions/resetInheritance" || fail "reset inheritance"
[[ $(visible) == 3 ]] || fail "reset inheritance shows the folder"

# Jobs: live events stream (server-sent events), operations of nobody are not found.
EVENTS=$(curl -sN --max-time 2 "${AUTH[@]}" "$BASE/v1.0/me/events" || true)  # the stream only ends at the timeout
grep -q '^event: connected' <<<"$EVENTS" || fail "live events stream: $EVENTS"
[[ $(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$BASE/v1.0/operations/$(python3 -c 'import uuid; print(uuid.uuid4())')") == 404 ]] || fail "unknown operation"

# Burst: parallel writes (each also queues an event and a workflow check) and filtered reads.
seq 1 2000 | xargs -P 16 -I{} curl -sf -o /dev/null "${AUTH[@]}" "${JSON[@]}" "$ITEMS" \
  -d '{"fields":{"title":"Load {}","amount":{}}}' || fail "write burst"
PEAK=$(rss_mb)
seq 1 500 | xargs -P 16 -I{} curl -sf -o /dev/null "${AUTH[@]}" -G "$ITEMS" \
  --data-urlencode '$filter=fields/amount gt {}' --data-urlencode '$top=50' || fail "read burst"
PEAK=$(( $(rss_mb) > PEAK ? $(rss_mb) : PEAK ))
# Subscribers work off the burst's events for a while after it: measure that too.
for _ in $(seq 1 10); do sleep 1; PEAK=$(( $(rss_mb) > PEAK ? $(rss_mb) : PEAK )); done
AFTER=$(rss_mb)
PEAK=$(( AFTER > PEAK ? AFTER : PEAK ))

echo "Memory: idle ${IDLE} MB (budget ${IDLE_BUDGET_MB}), under load ${PEAK} MB (budget ${LOAD_BUDGET_MB})"
[[ "$IDLE" -lt "$IDLE_BUDGET_MB" ]] || fail "idle memory ${IDLE} MB over budget"
[[ "$PEAK" -lt "$LOAD_BUDGET_MB" ]] || fail "memory under load ${PEAK} MB over budget"
! grep -E "Unhandled exception|fail:" "$LOG" || fail "errors in the host log"
echo "AOT smoke test passed."
