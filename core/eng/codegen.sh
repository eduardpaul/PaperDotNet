#!/usr/bin/env bash
# Writes the Wolverine handler code (Internal/Generated) that the Native AOT build compiles in (ADR-0039).
# Run after adding or changing a subscriber (a *Subscriber class) or an event it handles; commit the result.
set -euo pipefail
cd "$(dirname "$0")/.."
host=src/PaperDotNet.Core.Host
rm -rf "$host/Internal/Generated"
(cd "$host" && dotnet run -- codegen write)
