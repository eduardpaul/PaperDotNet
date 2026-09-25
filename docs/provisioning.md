# Provisioning templates

A template captures the configuration of a whole tenant or of one workspace as
XML (PRV-01…05). Use it to move a setup from test to production, to reuse a
ready-made workspace, or to keep configuration in source control. The design
is described in [ADR-0017](adr/0017-phase-5-scope.md).

## What is included

| Section | Contents | Owner |
|---|---|---|
| `Extensions` | Enabled extensions and their settings (values as JSON) | ExtensionHost |
| `Groups` | Groups and their members (by user name) | Identity |
| `Roles` | Custom roles, their scopes and assignments (tenant templates only) | Identity |
| `TermGroups` | Term groups, term sets, term trees with labels and synonyms | Taxonomy |
| `ContentTypes` | Custom content types with fields; built-in and extension ones by key | Lists |
| `Workspace` | Name, description and members with roles | Workspaces |
| `List` | Settings, content types, views and list permissions | Lists |
| `doc:LibrarySettings` | Duplicate policy and processing settings of a library | Documents |

Extensions add their own sections in their own XML namespaces.

Templates never contain the following:
- items or documents (planned for P7, PRV-04);
- item-level permissions;
- personal workspaces or system lists;
- secrets.

## References by name

Ids differ between tenants, so templates refer to objects by name:

- content types by name, or by `Key` for built-in and extension content types;
- term sets as `Group/Set`;
- lookup lists as `Workspace/List`;
- groups by name and users by user name.

A user who does not exist on the target is skipped with a warning.

View filters that contain ids (e.g. term ids) are copied as they are and may
not match on another tenant.

## Example

```xml
<Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Workspace">
  <Parameters>
    <Parameter Name="Project" Default="Apollo" />
  </Parameters>
  <ContentTypes>
    <ContentType Name="Invoice">
      <Field Name="amount" Type="currency" CurrencyCode="EUR" Required="true" />
      <Field Name="vendor" Type="lookup" LookupList="{parameter:Project}/Vendors" />
    </ContentType>
  </ContentTypes>
  <Workspaces>
    <Workspace Name="{parameter:Project}">
      <Members><Member User="alice" Role="Owner" /></Members>
      <Lists>
        <List Name="Vendors" />
        <List Name="Invoices" Kind="Library">
          <ContentTypes><ContentTypeRef Name="Invoice" /></ContentTypes>
          <Views>
            <View Name="Open" Default="true" OrderBy="fields/amount desc">
              <Column>title</Column><Column>amount</Column>
            </View>
          </Views>
          <Permissions Unique="false" />
          <doc:LibrarySettings xmlns:doc="urn:paperdotnet:documents:1" OcrLanguages="deu+eng" />
        </List>
      </Lists>
    </Workspace>
  </Workspaces>
</Template>
```

## API

| Request | Description |
|---|---|
| `GET /v1.0/provisioning/schema` | The XSD (anonymous) |
| `GET /v1.0/provisioning/export` | Tenant template (`template.read`) |
| `GET /v1.0/provisioning/export?workspaceId=…` | Workspace template, with the tenant-level objects it uses (also needs Manage access to the workspace) |
| `POST /v1.0/provisioning/apply` | Apply the XML body (`application/xml`, `template.manage`) |

Query options for `apply`:

- `dryRun=true` lists the planned changes and writes nothing.
- `workspaceId=…` applies a workspace template to an existing workspace. The
  workspace keeps its name.
- `parameters[Name]=value` sets a parameter.

`apply` answers `{ dryRun, changes: [{ action, kind, name, detail }], warnings }`.

## Rules

- **Validated first.** A template is checked against the XSD, then fully dry
  run, before anything is written. Errors are returned as a validation problem
  and carry line numbers.
- **Idempotent and additive.**
  - Objects are matched by name. Missing ones are created and changed
    attributes are updated.
  - Nothing is deleted: fields, terms, views, lists and members stay.
  - Applying the same template twice changes nothing.
  - Exceptions: a list's `Permissions` element sets inheritance and grants
    exactly, and an extension's settings are replaced.
- **Not atomic across modules.** If the real run fails after a successful dry
  run (rare, e.g. a concurrent change), part of the template may be applied.
  Fix the problem and apply the template again.
