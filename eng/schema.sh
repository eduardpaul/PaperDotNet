#!/usr/bin/env bash
# Schema changes (ADR-0039). EF Core cannot run migrations under Native AOT, so each migration of each module is turned
# into a SQL script embedded in PaperDotNet.Persistence.Sqlite and applied by the host on start (SqliteDatabase).
#
#   eng/schema.sh add <Name>   add a migration to every module whose model changed, then regenerate the scripts
#   eng/schema.sh scripts      regenerate the scripts from the existing migrations
#   eng/schema.sh check        fail when a module's model has changes without a migration
set -euo pipefail
cd "$(dirname "$0")/.."

project=src/Migrations/PaperDotNet.Migrations.Sqlite
out=src/BuildingBlocks/PaperDotNet.Persistence.Sqlite/Schema
# Module DbContexts and the folder of their migrations.
contexts=(IdentityDbContext:Identity ListsDbContext:Lists AuditDbContext:Audit WorkflowsDbContext:Workflows JobsDbContext:Jobs WorkspacesDbContext:Workspaces ExtensionsDbContext:ExtensionHost)
dotnet tool restore >/dev/null
dotnet build "$project" >/dev/null

pending() { ! dotnet ef migrations has-pending-model-changes -p "$project" -s "$project" -c "$1" --no-build >/dev/null 2>&1; }

case "${1:-scripts}" in
  add)
    name=${2:?migration name}
    for entry in "${contexts[@]}"; do
      context=${entry%%:*}
      if pending "$context"; then
        dotnet ef migrations add "$name" -p "$project" -s "$project" -c "$context" -o "Generated/${entry##*:}" --no-build
        dotnet build "$project" >/dev/null
      fi
    done
    ;;
  check)
    status=0
    for entry in "${contexts[@]}"; do
      if pending "${entry%%:*}"; then echo "${entry%%:*} has model changes without a migration" >&2; status=1; fi
    done
    exit $status
    ;;
  scripts) ;;
  *) echo "usage: $0 add <Name> | scripts | check" >&2; exit 2 ;;
esac

mkdir -p "$out"
rm -f "$out"/*.sql
for entry in "${contexts[@]}"; do
  context=${entry%%:*}
  previous=0
  for id in $(dotnet ef migrations list -p "$project" -s "$project" -c "$context" --no-build --no-connect --json --prefix-output \
      | sed -n 's/^data: *//p' | python3 -c 'import json,sys; [print(m["id"]) for m in json.load(sys.stdin)]'); do
    dotnet ef migrations script "$previous" "$id" -p "$project" -s "$project" -c "$context" --no-build --no-transactions -o "$out/$id.sql"
    previous=$id
  done
done
echo "Scripts in $out:"; ls "$out"
