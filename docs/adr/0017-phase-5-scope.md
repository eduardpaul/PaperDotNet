# ADR-0017: Phase 5 scope and provisioning templates

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Phase 5 ("collaboration, automation and integrations") collects about 35
features. Some of them need large protocol implementations (WebDAV, CalDAV,
CardDAV) for which no mature MIT/Apache server library exists for .NET, and our
rule is not to hand-roll protocol code. Provisioning templates (PRV, idea 0021)
touch the configuration of almost every module.

## Decision

### Scope and order

- Slices in this order: **5a provisioning**, **5b automation** (rules,
  workflows, extension triggers and actions, path templates), then sharing,
  collaboration and sync API, notification channels, smart folders, page
  operations and S3, MCP and SDKs, external OIDC and quotas.
- **Workflows (EVT-08)** are part of P5. The engine was planned to be Elsa 3; ADR-0018
  replaces it (Elsa's current dependencies are not license-compatible); ADR-0019 runs workflows
  on our existing messaging without an engine.
- **WebDAV, CalDAV and CardDAV (API-10, CAL-05, CAL-06) move to P7.**
- **Email to inbox (DOC-13) moves to the backlog.** The email notification
  channel (NTF-04) stays in P5.

### Provisioning templates (5a)

- **Each module owns its section.** A template is XML in the namespace
  `urn:paperdotnet:template:1` (schema version 1.0). Modules contribute
  sections through `ITemplateHandler` (`PaperDotNet.Provisioning.Contracts`,
  part of the SDK). The Provisioning module parses, validates, substitutes
  parameters and runs the handlers; it never touches other modules' tables.
- **Levels.**
  - Tenant level: extensions, groups, roles, term groups and content types, in
    that order.
  - Workspace level (`<Workspace>`) and list level (`<List>`): each is created
    or found by the owning module through `ITemplateContainer` (Workspaces,
    Lists).
  - Workspace and list containers carry their own children (members; content
    types, views, permissions). Handlers of other modules and extensions add
    sections in their own XML namespace at any level, e.g. the Documents
    library settings.
- **References by name, never by id.** References are resolved on the target:
  - content types by name, or by key for built-in and extension ones;
  - term sets as `Group/Set`;
  - lookup lists as `Workspace/List`;
  - groups by name and users by user name.

  Handlers share resolved and planned ids through the template context. A
  lookup field whose list is created later in the same template is completed
  in a deferred step.
- **Idempotent and additive.**
  - Objects are matched by name. Missing ones are created and changed
    attributes are updated.
  - Nothing that is missing from the template is deleted: fields, terms,
    views, lists and members stay.
  - Two exceptions make the object match the template exactly: the grants of a
    list whose `<Permissions>` element is present, and an extension's settings.
- **Dry run first, always.**
  - Every apply first runs all handlers in dry-run mode, which records the
    planned changes (create/update) and validates everything, including field
    definitions and view queries.
  - Nothing is written unless the dry run succeeds. `dryRun=true` returns only
    the plan.
  - The apply is not one transaction across modules. A failure during the real
    run leaves part of the template applied, and running it again completes it.
- **Parameters.** `<Parameters>` declares names with optional defaults.
  `{parameter:Name}` in any attribute or text is replaced before validation.
- **Schema.**
  - The XSD is embedded and served at `GET /v1.0/provisioning/schema`.
  - Validation errors carry line numbers.
  - Extension sections are validated laxly (`xs:any namespace="##other"`).
- **Workspace scope.** Exporting a workspace also exports the tenant-level
  objects it depends on (content types, term sets, groups, extensions), so the
  template is self-contained. Personal workspaces (Home) and system lists are
  never exported.
- **Access.** New scopes `template.read` (export) and `template.apply`. Neither
  is granted to members. A workspace-scope export or apply also needs Manage
  access to the workspace.
- **Not included** (PRV-04, P7): items, documents and item-level permissions.
  Secrets such as webhook secrets and OAuth clients are not included either.

## Consequences

- A new configurable module needs a template handler to be portable. The
  handler contract is public, so extensions get the same power (PRV-05).
  Extension handlers only run where the extension is enabled.
- Because templates are additive, removing something from a template does not
  remove it from targets. Deletion stays a manual action.
