#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$repo_root"

# Linux/WSL safeguard: the scope covers build tools, the generator and its server child.
# Docker containers are outside this scope; PerfDatabase caps its own PostgreSQL container.
if ! command -v systemd-run >/dev/null || ! systemctl --user is-active default.target >/dev/null; then
  echo "This wrapper requires a running user systemd instance. Apply equivalent process/container limits on other hosts." >&2
  exit 1
fi

available_kib=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
if (( available_kib < 2 * 1024 * 1024 )); then
  echo "Less than 2 GiB RAM is available; stop other workloads before benchmarking." >&2
  exit 1
fi

export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-2}"
exec systemd-run --user --scope --quiet \
  -p MemoryMax=1G -p MemorySwapMax=0 -p CPUQuota=200% \
  bash tests/PaperDotNet.Performance/run.sh "$@"
