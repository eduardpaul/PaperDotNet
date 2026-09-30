# MCP server for AI assistants

PaperDotNet is an MCP server (API-08). AI assistants such as Claude, VS Code or
other MCP clients can search, read, create and update documents, tasks, events
and list items, and can upload and read library files. They work with the
permissions of the user who connects. Design:
[ADR-0021](adr/0021-mcp-and-sdks.md).

## Connecting

- **Endpoint:** `https://{your installation}/v1.0/mcp` (Streamable HTTP,
  stateless).
- **Authentication:** an OAuth access token or an API token as
  `Authorization: Bearer …`.
  - The token needs the scope `mcp.use`.
  - Each tool also needs its own scope, e.g. `list.read` or `list.write`.
  - An API token limited to `mcp.use list.read search.read` gives an assistant
    read-only access.
- **Tenant:** send the tenant header (`X-Tenant`) if your installation does
  not select the tenant by host name.

Example client configuration (Streamable HTTP with a header):

```json
{
  "mcpServers": {
    "paperdotnet": {
      "type": "http",
      "url": "https://dms.example.com/v1.0/mcp",
      "headers": { "Authorization": "Bearer pdn_…" }
    }
  }
}
```

## Tools

| Tool | Scope | What it does |
|---|---|---|
| `search` | `search.read` | Full-text search over everything the user can read (documents including their text, tasks, events, items) |
| `list_workspaces` | `workspace.read` | The user's workspaces |
| `get_home` | `list.read` | The user's personal workspace and its Documents and Inbox libraries |
| `list_lists` | `list.read` | Lists and libraries, with template (`tasks`, `events`, `documents`, …), `isLibrary`, and content type id and key |
| `describe_list` | `list.read` | Columns of a list: name, type, required, choices. Call this before creating or editing |
| `query_items` | `list.read` | Items of a list (every folder; folders themselves are omitted) with an OData `filter`/`orderBy`. Pages with `top` (up to 1000, default 50) and `cursor` |
| `list_children` | `list.read` | Folders and items in one folder, with the same paging |
| `get_item` | `list.read` | One item with its fields, content type, version and access level |
| `create_item` | `list.write` | A new task, event or entry. `parentId` places it in a folder |
| `update_item` | `list.write` | Changes fields; `version` guards against lost updates |
| `create_folder` | `list.write` | A new folder |
| `ensure_folder` | `list.write` | The folder at a path such as `Invoices/2026`, created where it is missing |
| `move_item` | `list.write` | Moves an item or folder. Omit `folderId` for the list root |
| `delete_item` | `list.write` | Moves an item, or an empty folder, to the recycle bin |
| `upload_document` | `document.write` | Uploads a PDF, TIFF, JPEG or PNG (`contentBase64`) into a library and creates the item |
| `replace_document` | `document.write` | A new file version. Pass `sha256` from `get_file` |
| `get_file` | `document.read` | Name, type, size, sha256 and page count of the current file |
| `read_document` | `document.read` | Extracted text, page by page. `nextPage` continues a long document |
| `{extension}_…` | per tool | Tools of enabled extensions |

`query_items` and `list_children` return `nextCursor` when more rows match. Pass that value back as `cursor` with the same filter and order. When `nextCursor` is absent, the page is the last one.

Library files are not item fields. `upload_document` takes base64 because tool arguments are JSON and checks the bytes (not the extension); the library's workflows then read the text ([documents.md](documents.md)). `read_document` returns the text they stored; until then, or when a library does not read texts, it returns no pages and says so. A page longer than `maxCharacters` (default 24000) is cut off.

- **Listing:** the tool list only contains tools the caller's token allows and
  that are enabled in the tenant.
- **Errors:** errors such as "not found" or invalid arguments come back as tool
  errors that the assistant can read.
- **Data changes:** all writes go through the same pipeline as the API:
  validation, permissions, versions, events, search and workflows.

## Tools from modules and extensions (API-09)

A tool implements `IMcpTool` (Mcp.Contracts, part of the SDK):

- a name, description and JSON schema (`McpSchema.ObjectSchema(…)`);
- the scope it needs, and whether it only reads;
- `CallAsync(McpArguments, ct)`, which returns `McpToolResult.FromJson(…)` or
  `McpToolResult.Error(…)`.

How to register a tool:

- **Modules:** register it as a scoped `IMcpTool`.
- **Extensions:** call `builder.AddMcpTool<TTool>()`.
  - The name must start with the extension id, with `.` and `-` replaced by
    `_`, then `_`. For example, `samples.invoices` gets
    `samples_invoices_pending`.
  - The tool is offered only in tenants that enabled the extension.

Tools run as the calling user. Use the SDK services (`IListItemStore`, …),
which check permissions.
