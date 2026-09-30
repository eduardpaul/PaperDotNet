#!/usr/bin/env bash
# Schema changes of the AOT core (ADR-0039). EF Core cannot run migrations under Native AOT, so each migration is
# turned into a SQL script that the host embeds and applies on start (SqliteDatabase.MigrateAsync).
#
#   eng/schema.sh add <Name>   add a migration for model changes, then regenerate the scripts
#   eng/schema.sh scripts      regenerate the scripts from the existing migrations
#   eng/schema.sh check        fail when the model has changes without a migration
set -euo pipefail
cd "$(dirname "$0")/.."

project=src/PaperDotNet.Core.Migrations.Sqlite
out=src/PaperDotNet.Core.Host/Schema/Sqlite
dotnet tool restore >/dev/null

case "${1:-scripts}" in
  add)
    dotnet ef migrations add "${2:?migration name}" -p "$project" -s "$project" -o Generated
    ;;
  check)
    dotnet ef migrations has-pending-model-changes -p "$project" -s "$project"
    exit 0
    ;;
  scripts) ;;
  *) echo "usage: $0 add <Name> | scripts | check" >&2; exit 2 ;;
esac

mkdir -p "$out"
rm -f "$out"/*.sql
previous=0
for id in $(dotnet ef migrations list -p "$project" -s "$project" --no-connect --json --prefix-output | sed -n 's/^data: *//p' | python3 -c 'import json,sys; [print(m["id"]) for m in json.load(sys.stdin)]'); do
  dotnet ef migrations script "$previous" "$id" -p "$project" -s "$project" --no-build --no-transactions -o "$out/$id.sql"
  previous=$id
done
echo "Scripts in $out:"; ls "$out"
