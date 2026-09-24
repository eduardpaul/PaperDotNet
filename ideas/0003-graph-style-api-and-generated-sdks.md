# 0003: Microsoft Graph-style client API and generated multi-language SDKs

- **Status:** new
- **Area:** API / Integrations
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

1. The client REST API should look and behave like the Microsoft Graph API.
2. The client SDKs should be generated the same way Microsoft generates the
   Graph SDKs, so we can support many languages (JavaScript/TypeScript, Python,
   and more).

## Why / problem it solves

- Developers who know Graph/SharePoint can pick up the API quickly.
- One API description produces consistent SDKs for every language without
  writing each one by hand. This matters for the extension ecosystem.

## Examples / references

- Microsoft Graph conventions:
  - resource paths like `/workspaces/{id}/lists/{id}/items/{id}`, `/me/drive`
  - OData-style query options: `$select`, `$filter`, `$expand`, `$orderby`,
    `$top`, `$skip`, `$count`, `$search`
  - `@odata.nextLink` paging, `$batch` requests, delta queries (`/delta`)
  - change notifications (webhook subscriptions), standard error format
  - versioned endpoints (`/v1.0`, `/beta`)
- SharePoint resources in Graph: `sites/{id}/lists/{id}/items`, `drives`,
  `driveItems`. These map closely to our lists and libraries model.
- Microsoft generates the Graph SDKs with **Kiota** (open source), using the
  Graph OpenAPI description. Kiota supports C#, TypeScript, Python, Go, Java,
  PHP and Ruby.

## Notes

<!-- Open questions to settle during review:
     - Full OData ($filter grammar) or a Graph-like subset? In ASP.NET Core we
       could use Microsoft.AspNetCore.OData, or write a custom parser.
     - Dynamic list schemas: are SDKs generic (items with a `fields` dictionary,
       like Graph's listItem.fields) or typed per content type (generated
       per workspace)?
     - Which languages ship first? (JS/TS and Python requested; .NET is almost free.)
     - Where do SDKs live: this repo, or separate repos/packages (npm, PyPI, NuGet)?
     - Relation to architecture-vision.md section 4 (API) and extension point
       6 (remote extensions call back via the REST API). -->
