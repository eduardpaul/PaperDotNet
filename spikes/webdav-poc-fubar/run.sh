#!/usr/bin/env bash
# Builds the FubarDev.WebDavServer PoC, starts it read-only and read-write, runs check.py and every litmus suite against each.
# Usage: ./run.sh <path to a clone of FubarDevelopment/WebDavServer, branch release/2.0>
# Env:   DEAD_PROPERTIES=true keeps client (dead) properties in memory for the litmus run.
set -euo pipefail
cd "$(dirname "$0")"
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
extra=(-p:FubarPath="$1" -p:FolderMove="${FOLDER_MOVE:-false}")
dotnet build -nologo -v q -c Release "${extra[@]}" >/dev/null
dll=$PWD/bin/Release/net10.0/WebDavPocFubar.dll
logs=$(mktemp -d)

start() { # port readonly
  Poc__ReadOnly=$2 Poc__DeadProperties=${DEAD_PROPERTIES:-false} ASPNETCORE_URLS=http://127.0.0.1:$1 dotnet "$dll" >"$logs/poc-$1.log" 2>&1 &
  pid=$!
  for _ in $(seq 1 50); do curl -s -o /dev/null -X OPTIONS "http://127.0.0.1:$1/dav/" && break; sleep 0.2; done
}

echo "== read-only =="
start 5099 true; python3 ../webdav-poc/check.py http://127.0.0.1:5099 ro || true; kill $pid; wait $pid 2>/dev/null || true
echo; echo "== read-write =="
start 5098 false; python3 ../webdav-poc/check.py http://127.0.0.1:5098 rw || true; kill $pid; wait $pid 2>/dev/null || true

if command -v litmus >/dev/null; then
  echo; echo "== litmus (read-write, fresh server, empty library) =="
  start 5097 false
  # litmus stops at the first failing suite, so each suite runs on its own; it writes debug.log into the cwd.
  (cd "$logs" && for suite in basic copymove props locks http; do
     TESTS=$suite litmus http://127.0.0.1:5097/dav/Projects/Archive/ anyone pdn_poc_0123456789abcdef 2>&1 | grep -E "FAIL|summary" || true
   done)
  kill $pid; wait $pid 2>/dev/null || true
fi
echo; echo "logs: $logs"
