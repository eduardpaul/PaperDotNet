# 0009: Automation rules engine (tags and other use cases) with Elsa Workflows

- **Status:** mapped
- **Area:** Automation / Extensions
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): EVT-07…09, TSK-06

## The idea

An automation and workflow engine built on
[Elsa Workflows](https://github.com/elsa-workflows/elsa-core), the open-source
workflow engine for .NET. It powers:

- **Tag automation** (idea 0008), such as:
  - auto-tag on upload based on OCR text, sender, file type or metadata
  - "when tagged X, do Y"
  - tag-driven filing (move to folder or list)
- **Other use cases**, such as:
  - document approval and review flows
  - task creation from documents or emails (idea 0002)
  - reminders and escalations for tasks and events
  - recurring tasks
  - retention and archiving
  - notifications
  - webhooks to external systems

## Why / problem it solves

- Papermerge's "automates" feature was removed. Users want rules that
  classify and file documents without manual work.
- SharePoint has Power Automate. PaperDotNet needs a built-in, self-hostable
  equivalent.
- Elsa is .NET-native, embeddable in ASP.NET Core, and extensible with custom
  activities. It fits the stack and the extension model.

## Examples / references

- Elsa Workflows 3:
  - workflows defined in C# code or as JSON
  - a visual designer (Elsa Studio)
  - triggers: HTTP, timer/cron, events
  - long-running workflows with bookmarks and resumption
  - custom activities
  - persistence via EF Core
- Architecture vision: extension point 7 "Automation triggers and actions" and
  roadmap phase 5 in [architecture-vision.md](../docs/architecture-vision.md).
- Similar: Power Automate, Paperless-ngx workflows/matching rules,
  Zapier/n8n.

## Notes

<!-- Open questions to settle during review:
     - Two levels:
         - simple "if this then that" rules in a friendly UI, for everyday
           users (e.g. tag rules)
         - full Elsa workflows with a visual designer, for power users/admins
       Do both compile down to Elsa workflows?
     - Custom Elsa activities for the platform:
         - triggers: item created/updated/deleted, tag added/removed,
           file uploaded, OCR completed, due date reached, schedule
         - actions: set field, add/remove tag, move, create task/event,
           send notification, call webhook, start OCR
     - Extensions contribute their own triggers and activities through the
       manifest. Extension point 7 maps to Elsa activities.
     - Hook into the event outbox (architecture section 4) so events start
       workflows reliably.
     - Security: workflows run as whom (owner, system, or triggering user)?
       Limits per tenant (idea 0005), and audit log of automated changes.
     - Designer UI: Elsa Studio is Blazor. Embed it, or build a lighter React
       rule builder (frontend decision 1)?
     - Licensing: confirm Elsa's license and version (v3) fit the product model.
     - Expose workflows via API/SDK (idea 0003) and MCP (idea 0004), e.g.
       "run workflow X on these items". -->
