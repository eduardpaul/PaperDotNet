# Client CLI (`pdn`)

`pdn` (API-13) works with a PaperDotNet installation from the command line:
- upload files, or whole folders with their structure;
- download files;
- search.

It is built on the C# SDK (`PaperDotNet.Client`). The server's admin CLI
(`paperdotnet migrate | backup | …`) is a separate tool.

```bash
dotnet tool install -g PaperDotNet.Cli      # or: dotnet run --project src/Tools/PaperDotNet.Cli --
export PAPERDOTNET_URL=https://dms.example.com
export PAPERDOTNET_TOKEN=pdn_…               # an API token (/v1.0/me/apiTokens) or an OAuth access token
export PAPERDOTNET_TENANT=acme              # only when the host name does not select the tenant
```

## Commands

| Command | What it does |
|---|---|
| `pdn workspaces` | Lists the workspaces you can see (id and name) |
| `pdn libraries -w <workspace>` | Lists the document libraries of a workspace |
| `pdn upload <paths>… -w <workspace> -l <library> [-f 2026/Invoices] [--languages deu+eng]` | Uploads files into a library. Folders are uploaded with their sub-folders, which become library folders under `-f`. Existing folders are reused, missing ones are created. |
| `pdn upload <paths>… --inbox` | Uploads into your Inbox (no folders) |
| `pdn download -w <workspace> -l <library> -i <item> [-o path]` | Downloads a document's current file |
| `pdn search "<query>" [--top 20] [--mode keyword\|semantic\|hybrid]` | Searches everything you can see. Hits show the page when a page matched. |

**Arguments:**
- Workspaces and libraries are given by id or name.
- `--url`, `--token` and `--tenant` override the environment.

**Upload rules:**
- Only PDF, TIFF, JPEG and PNG files are uploaded; other files are skipped
  with a message.
- The server's duplicate policy applies. A duplicate is reported, and a
  blocked duplicate counts as a failure.

**Exit codes:**
- `0`: everything worked.
- `1`: at least one file or request failed.
- `2`: missing connection settings.

Library uploads accept `folderId` on the API as well
(`POST …/lists/{id}/documents`), which is how `pdn` places files in folders.
