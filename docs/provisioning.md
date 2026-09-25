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
- items or documents, unless exported as a package (below);
- item-level permissions (packages carry them, below);
- personal workspaces or system lists;
- secrets.

## Packages with content (PRV-04)

A **package** is a zip with the template and the content of its lists. Use it
to ship a ready-made solution with sample or reference data, or to copy a
workspace with its data. Design: [ADR-0028](adr/0028-template-packages.md).

```text
template.xml                   the template, with an Items (and doc:Files) section per list
content/items-<list>.json      the list's items and folders
content/files-<list>.json      which item has which file (libraries)
files/<sha256>                 file contents, each stored once
```

**Export:** `GET /v1.0/provisioning/export?workspaceId=…&includeContent=true`
returns `application/zip`.

**Apply:** `POST /v1.0/provisioning/apply` with the zip as `application/zip`
(up to 512 MB) takes the same options as for XML. Larger packages, and
exports of whole tenants, go through [export and import](export-and-import.md).

**Items:**
- **Values are portable:**
  - terms as `Group/Set/Term` paths;
  - keywords as text;
  - people as user names;
  - lookups as `{ "list": "Workspace/List", "key": "…" }`.
- **Unknown on the target:** users or terms that don't exist on the target are
  left out, with a warning.
- **Keys and repeats:** every item has a `key`. An item created from a package
  gets an id derived from the target list and that key, so applying a package
  again creates nothing twice.
- **Additive:** existing items are never changed or deleted.
- **Normal write path:** items are created with validation, mutators, events,
  search and automations.
- **Order:** folders come before their contents. Lookups are set once every
  list of the package is filled.

**History and access** ([ADR-0029](adr/0029-papermerge-import.md)):
- **Stamps:** items keep who created and changed them, and when
  (`created`, `createdBy`, `modified`, `modifiedBy`).
- **Permissions:** items and folders with unique permissions carry them as
  user or group names with a level.

**Files:**
- **Versions:** every file version is included, oldest first, with its
  source, languages, stamps and page texts.
- **Processing:** versions with page texts, or that were processed at the
  source, are not processed again. Otherwise files are checked and processed
  like uploads (type, size, OCR).
- **Existing files:** an item that already has a file keeps it.

**Checks:** an `Items` or `doc:Files` section in a plain XML template (no
package) is an error, and so is a package that lacks a document its template
names.

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
