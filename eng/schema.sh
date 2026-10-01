#!/usr/bin/env bash
# Schema changes (ADR-0039). EF Core cannot run migrations under Native AOT, so each migration of each module is turned
# into a SQL script embedded in PaperDotNet.Persistence.Sqlite and applied by the host on start (SqliteDatabase).
# Extension tables (EXT-07) work the same way: migrations in the extension's .Migrations.Sqlite project, SQL in the
# extension's Schema/ folder (embedded by src/Extensions/Extension.props).
#
#   eng/schema.sh add <Name>   add a migration to every module whose model changed, then regenerate the scripts
#   eng/schema.sh scripts      regenerate the scripts from the existing migrations
#   eng/schema.sh check        fail when a module's model has changes without a migration
set -euo pipefail
cd "$(dirname "$0")/.."

project=src/Migrations/PaperDotNet.Migrations.Sqlite
out=src/BuildingBlocks/PaperDotNet.Persistence.Sqlite/Schema
# Module DbContexts and the folder of their migrations.
contexts=(IdentityDbContext:Identity ListsDbContext:Lists AuditDbContext:Audit WorkflowsDbContext:Workflows JobsDbContext:Jobs WorkspacesDbContext:Workspaces ExtensionsDbContext:ExtensionHost NotificationsDbContext:Notifications CollaborationDbContext:Collaboration NotesDbContext:Notes TasksDbContext:Tasks CalendarDbContext:Calendar TaxonomyDbContext:Taxonomy SearchDbContext:Search ProvisioningDbContext:Provisioning AiWorkflowsDbContext:AiWorkflows)
# Extension DbContexts: migrations project, DbContext and the extension's folder.
extensions=(samples/PaperDotNet.Samples.Invoices.Migrations.Sqlite:InvoicesDbContext:samples/PaperDotNet.Samples.Invoices)
dotnet tool restore >/dev/null
dotnet build "$project" >/dev/null
for entry in "${extensions[@]}"; do dotnet build "${entry%%:*}" >/dev/null; done

pending() { ! dotnet ef migrations has-pending-model-changes -p "${2:-$project}" -s "${2:-$project}" -c "$1" --no-build >/dev/null 2>&1; }

# The SQL of every migration of a DbContext: scripts <project> <context> <folder>
scripts() {
  local previous=0 id
  for id in $(dotnet ef migrations list -p "$1" -s "$1" -c "$2" --no-build --no-connect --json --prefix-output \
      | sed -n 's/^data: *//p' | python3 -c 'import json,sys; [print(m["id"]) for m in json.load(sys.stdin)]'); do
    dotnet ef migrations script "$previous" "$id" -p "$1" -s "$1" -c "$2" --no-build --no-transactions -o "$3/$id.sql"
    previous=$id
  done
}

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
    for entry in "${extensions[@]}"; do
      IFS=: read -r ext context _ <<<"$entry"
      if pending "$context" "$ext"; then
        dotnet ef migrations add "$name" -p "$ext" -s "$ext" -c "$context" -o Generated --no-build
        dotnet build "$ext" >/dev/null
      fi
    done
    ;;
  check)
    status=0
    for entry in "${contexts[@]}"; do
      if pending "${entry%%:*}"; then echo "${entry%%:*} has model changes without a migration" >&2; status=1; fi
    done
    for entry in "${extensions[@]}"; do
      IFS=: read -r ext context _ <<<"$entry"
      if pending "$context" "$ext"; then echo "$context has model changes without a migration" >&2; status=1; fi
    done
    exit $status
    ;;
  scripts) ;;
  *) echo "usage: $0 add <Name> | scripts | check" >&2; exit 2 ;;
esac

mkdir -p "$out"
rm -f "$out"/*.sql
for entry in "${contexts[@]}"; do
  scripts "$project" "${entry%%:*}" "$out"
done
echo "Scripts in $out:"; ls "$out"
for entry in "${extensions[@]}"; do
  IFS=: read -r ext context folder <<<"$entry"
  mkdir -p "$folder/Schema"
  rm -f "$folder/Schema"/*.sql
  scripts "$ext" "$context" "$folder/Schema"
  echo "Scripts in $folder/Schema:"; ls "$folder/Schema"
done
