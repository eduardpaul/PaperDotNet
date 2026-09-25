# ADR-0021: MCP server and generated SDKs

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Phase 5h brings:

- an MCP endpoint, so AI assistants can work with a user's data (API-08);
- MCP tools from extensions (API-09);
- official SDKs for C#, TypeScript and Python (API-03).

The guiding principle: use mature libraries, never hand-roll protocol code, and
put nothing between the assistant and the user's permissions.

## Decision

- **MCP with the official C# SDK** (`ModelContextProtocol.AspNetCore`,
  Apache-2.0).
  - It is mapped at `/v1.0/mcp` in stateless Streamable HTTP mode.
  - It goes through the normal pipeline: tenant resolution, authentication,
    the tenant guard, and the scope `mcp.use`.
  - Stateless mode needs no session store and works with several nodes.
- **Our own tool contract** `IMcpTool` in `PaperDotNet.Mcp.Contracts` (part of
  the extension SDK), so modules and extensions do not depend on the MCP
  library.
  - The server implements the `tools/list` and `tools/call` handlers over all
    registered tools.
  - It filters by tenant availability (extensions:
    `IExtensionBuilder.AddMcpTool`, gated per tenant like other extension
    points) and by the caller's scopes (the same authorization policies as
    endpoints).
  - Tools run in the request scope as the caller and use the SDK services,
    which check item permissions.
- **Tool names** follow `^[A-Za-z0-9_-]{1,64}$`, which common assistant APIs
  require. Extension tools start with the extension id with `_` instead of `.`
  and `-`.
- **Built-in tools:** `search` (Search module), `list_workspaces`,
  `list_lists`, `query_items`, `get_item`, `create_item` and `update_item`.
  Documents, tasks and events are list items, so these generic tools cover
  them.
- **SDKs are generated with Kiota** (MIT; runtime libraries MIT) from a
  committed `sdk/openapi.json`.
  - A test compares the committed document with the live one. It rewrites the
    document when `PAPERDOTNET_UPDATE_OPENAPI=1` is set.
  - `sdk/generate.sh` generates all three clients.
  - The generated code is committed, together with a small factory per
    language (token and tenant).
  - The C# client is part of the solution, and an integration test calls the
    API with it.

## Consequences

- API changes need a refreshed `openapi.json` and regenerated clients; the test
  enforces the first.
- Kiota clients are verbose (one request builder per path segment), but they
  are complete and consistent across languages. Hand-written convenience
  layers can come later.
- MCP resources and prompts are not offered yet; tools cover the user stories.
