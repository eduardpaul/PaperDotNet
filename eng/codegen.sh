#!/usr/bin/env bash
# Writes the Wolverine handler code (Internal/Generated) that the Native AOT build compiles in (ADR-0039).
# Run after adding or changing a subscriber (a *Subscriber class) or a message it handles; commit the result.
set -euo pipefail
cd "$(dirname "$0")/.."
host=src/PaperDotNet.Host
rm -rf "$host/Internal/Generated"
dotnet build "$host" >/dev/null
# Wolverine's command line prints its errors only to a terminal: run it under `script` when there is none.
if [[ -t 1 ]]; then
  (cd "$host" && dotnet bin/Debug/net11.0/paperdotnet.dll codegen write)
else
  (cd "$host" && script -qec "dotnet bin/Debug/net11.0/paperdotnet.dll codegen write" /dev/null)
fi
rm -rf "$host/data"
ls "$host/Internal/Generated/WolverineHandlers/"*.cs >/dev/null || { echo "codegen wrote no handlers" >&2; exit 1; }
