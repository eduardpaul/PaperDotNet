# Export and import

Export a workspace, or a whole tenant, with its content into a package, and
import the package elsewhere (PLT-13). The package is an open format: a zip
with the template XML (validated by the published XSD), JSON documents with the
items, and the files. See [provisioning.md](provisioning.md#packages-with-content-prv-04)
for its contents and [ADR-0028](adr/0028-template-packages.md) for the design.

## What is included

- **Configuration:** everything a template carries. That covers content types,
  term sets, groups, roles, workspaces with members, lists with views and
  permissions, library settings, smart folders, automations and extension
  settings.
- **Content:** list items and folders, and the current file of each document.
- **Not included:**
  - file versions other than the current one, item history and comments;
  - item-level permissions and personal workspaces;
  - users. People are referenced by user name, so create the users on the
    target first.

## API

| Request | Description |
|---|---|
| `POST /v1.0/portability/exports` `{ "workspaceId": … }` | Starts an export: `202` with the operation. Leave out `workspaceId` to export the whole tenant (administrators) |
| `GET /v1.0/portability/exports` | Your exports |
| `GET /v1.0/portability/exports/{id}` | `ready`, `size`, `expiresAt` and `packageUrl` |
| `GET /v1.0/portability/exports/{id}/package` | Download the package (`application/zip`) |
| `DELETE /v1.0/portability/exports/{id}` | Delete it now |
| `POST /v1.0/portability/imports` | Upload a package (`application/zip`) and import it as an operation. Query: `dryRun=true`, `workspaceId` (apply a workspace package to that workspace), `parameters[Name]=value` |

**Import results:** the import operation's result lists the `changes` and
`warnings`. When the package is invalid, the result lists `errors` instead.

**Permissions:**
- Exports need `template.read`; imports need `template.manage`.
- With a `workspaceId`, the caller also needs Manage access to that workspace.
- An export can only be read and downloaded by the user who made it.

**Retention and size:**
- Exports are kept for 7 days (`Provisioning:ExportRetentionDays`).
- Imports may be up to 4 GB (`Provisioning:MaxImportBytes`).

## Command line

```bash
paperdotnet export --tenant acme --user admin [--workspace "Finance"] [-o acme.zip]
paperdotnet import acme.zip --tenant other --user admin [--dry-run]
```

The commands run as the given user, with that user's access. The import prints
the changes and warnings.

## Rules

Imports follow the rules of templates:
- **Idempotent and additive:** what is missing is created and nothing is
  deleted. Items created by an earlier import are found again, so importing
  twice creates nothing twice.
- **Existing items:** existing items are left as they are.
- **Normal write path:** items are validated, indexed and seen by automations.
- **Missing users and terms:** users or terms that don't exist on the target
  are left out, with a warning.
