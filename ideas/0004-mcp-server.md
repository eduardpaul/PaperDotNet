# 0004: MCP server (supporting the extensibility features)

- **Status:** mapped
- **Area:** API / Integrations / Extensions
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): API-08, API-09, IAM-02

## The idea

Ship a built-in MCP (Model Context Protocol) server so AI assistants and agents
can work with the app: search, read and create documents, tasks, events and
list items. The MCP server must support the extensibility features, so tools
and data contributed by extensions become available through MCP too.

## Why / problem it solves

- Lets users drive the app from AI clients (Claude, IDEs, agents), e.g.
  "file this invoice", "what tasks are due this week?",
  "summarize the contracts tagged X".
- Extensions get AI access for free instead of each building their own
  integration.

## Examples / references

- MCP spec: tools, resources, prompts; transports: stdio and streamable HTTP.
- Official C# SDK: `ModelContextProtocol` / `ModelContextProtocol.AspNetCore`
  NuGet packages, which can be hosted inside the ASP.NET Core API.

## Notes

<!-- Open questions to settle during review:
     - New extension point: extensions contribute MCP tools/resources/prompts in
       their manifest (e.g. "contributes.mcp"). Or tools are derived automatically
       from commands and API endpoints (see idea 0003).
     - Generic tools from the lists engine (query_items, create_item, get_schema)
       so any content type works without custom code.
     - Auth: OAuth for remote MCP clients, scoped to the user's permissions and
       the tenant (see idea 0005). Also API tokens.
     - Safety: confirmation for destructive tools, audit log of MCP actions.
     - Resources: expose documents (OCR text) and item content as MCP resources. -->
