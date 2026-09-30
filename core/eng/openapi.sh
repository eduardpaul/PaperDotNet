#!/usr/bin/env bash
# Writes the OpenAPI description of the core API to sdk/openapi.json (input of the Kiota SDKs).
set -euo pipefail
cd "$(dirname "$0")/.."
PORT=${PORT:-5098}
DATA=$(mktemp -d)
dotnet build src/PaperDotNet.Core.Host -c Release >/dev/null
Storage__DataPath="$DATA" ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
  dotnet src/PaperDotNet.Core.Host/bin/Release/net11.0/paperdotnet.dll >"$DATA/host.log" 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true; wait $PID 2>/dev/null || true; rm -rf "$DATA"' EXIT
for _ in $(seq 1 150); do curl -sf "http://127.0.0.1:$PORT/health" >/dev/null && break; sleep 0.2; done
curl -sf "http://127.0.0.1:$PORT/openapi/v1.json" | python3 -m json.tool --indent 2 > sdk/openapi.json
echo "Wrote sdk/openapi.json"
