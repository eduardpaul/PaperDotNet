#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$repo_root"

dotnet build tests/PaperDotNet.Performance -c Release -m:1 --disable-build-servers
if [[ -z "${PERF_HOST_PATH:-}" ]]; then
  publish_args=()
  if [[ -n "${PERF_PUBLISH_RID:-}" ]]; then
    publish_args+=(--runtime "$PERF_PUBLISH_RID" --self-contained false)
  fi
  dotnet publish src/PaperDotNet.Host -c Release -m:1 --disable-build-servers \
    -o "$repo_root/artifacts/performance-host" "${publish_args[@]}"
  export PERF_HOST_PATH="$repo_root/artifacts/performance-host/paperdotnet.dll"
fi

exec dotnet tests/PaperDotNet.Performance/bin/Release/net10.0/PaperDotNet.Performance.dll "$@"
