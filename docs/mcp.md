# MCP server for AI assistants

PaperDotNet is an MCP server (API-08). AI assistants such as Claude, VS Code or
other MCP clients can search, read, create and update documents, tasks, events
and list items. They work with the permissions of the user who connects. Design:
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
| `list_lists` | `list.read` | Lists and libraries, with their template (`tasks`, `events`, `documents`, …) |
| `query_items` | `list.read` | Items of a list with an OData `filter`/`orderBy` |
| `get_item` | `list.read` | One item with all fields |
| `create_item` | `list.write` | A new task, event or entry |
| `update_item` | `list.write` | Changes fields; `version` guards against lost updates |
| `{extension}_…` | per tool | Tools of enabled extensions |

- **Listing:** the tool list only contains tools the caller's token allows and
  that are enabled in the tenant.
- **Errors:** errors such as "not found" or invalid arguments come back as tool
  errors that the assistant can read.
- **Data changes:** all writes go through the same pipeline as the API:
  validation, permissions, versions, events, search and automation.

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
