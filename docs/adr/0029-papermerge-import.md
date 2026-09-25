# ADR-0029: Import from Papermerge through a converter to a package

- **Status:** Accepted (implemented in slice 7d; see "As built" below)
- **Date:** 2026-09-25

## Context

People who move from Papermerge want to bring their archive: folders,
documents with all versions, OCR text, tags, document types, custom fields and
who can see what. Papermerge has no export format and no import/export command
(its CLI has `pm`, `docs` and `sign_url`). Its data is in two places.

**Database:** PostgreSQL only, SQLAlchemy models, schema versioned with
Alembic. Surveyed release: papermerge-core 3.6 (commit `5ed5dbf`, Alembic head
`v3_6_0__0013`).

| Area | Tables |
|---|---|
| Tree | `nodes` (id, title, ctype, lang, parent_id; polymorphic on ctype) → `folders` / `documents` (ocr, ocr_status, processing_status, document_type_id) |
| Content | `document_versions` (number, file_name, size, mime_type, checksum, is_original, source_version_id, creation_reason, text) → `pages` (number, lang, text) |
| Classification | `tags` (name, fg_color, bg_color, pinned, description) + `nodes_tags`; `document_types` (name, path_template) + `document_types_custom_fields` (position); `custom_fields` (name, type_handler, config JSONB) + `custom_field_values` (value JSONB and one typed column per kind) |
| People and access | `users`, `groups`, `users_groups`, `roles` / `permissions` / `users_roles`; `ownerships` (a user or group owns a node, tag, custom field or document type); `shared_nodes` (node → user or group + role); `special_folders` (home and inbox per owner) |
| History | audit columns: created, updated, deleted and archived (at and by) |

**Files:** under `media_root`, or in an S3 bucket. The layout is:
- `docvers/{id[0:2]}/{id[2:4]}/{version id}/{file name}` for each document
  version;
- `thumbnails/…` and `pages/…` (svg, jpg, hocr) for previews. These can be
  regenerated.

Our side already has an open, idempotent package format with import
(ADR-0028, PRV-04 / PLT-13).

## Decision

### Import: a converter that writes a PaperDotNet package

- **Separate tool, not a module.** It lives in `tools/PaperDotNet.Import.Papermerge`
  and is exposed as `paperdotnet import-papermerge --db <dsn> --media <path|s3>`.
  It reads Papermerge's database through its own read-only EF Core model of
  the tables above, using Npgsql. No raw SQL, and no provider packages in
  modules (ADR-0009). It reads files from the media root, or from S3 through
  the S3 blob store.
- **Pinned versions.** The tool reads `alembic_version` and refuses schema
  versions it was not tested with. It starts with Papermerge 3.5 and 3.6. Each
  new Papermerge release is a deliberate update of the model and the test
  fixture.
- **Output is a normal package** (ADR-0028): `template.xml` with workspaces,
  content types, term sets and automations, `content/*.json` with items, and
  `files/<sha256>`. It is loaded with the existing import:
  `paperdotnet import` or `/v1.0/portability/imports`. That gives us the dry
  run, warnings, progress and repeatable re-runs (item ids derived from keys;
  the Papermerge node id is the key). The package can also be checked or
  edited before it is imported.

### Mapping

| Papermerge | PaperDotNet |
|---|---|
| Home folder tree of a user or group | A Documents library with folders. By default one workspace per owner; optionally one shared "Papermerge" workspace with a top folder per owner |
| Inbox special folder | Folder `Inbox` in the same library |
| Document and versions | Library item with all file versions, in order. `is_original` and `creation_reason` become the version comment |
| `pages.text` | Page text of the stored file, so documents are **not OCRed again** |
| `lang` | Language of the file (OCR and stemming) |
| Tags | Terms in a term set `Tags` (color, pinned and description as term properties), set in a `keywords` field. Tags of different owners with the same name are merged by default; `--tag-prefix-owner` keeps them apart |
| Document types | Content types. Fields keep their `position` |
| Custom fields | short_text → text, text → note, integer / number → number, monetary → currency, date → date, datetime → dateTime, select → choice, multiselect → multi-choice, boolean → boolean, url → url, email → email, yearmonth → text (`yyyy-MM`) |
| `path_template` | Automation `itemAdded` + `item.file` when the Jinja template only uses plain field placeholders. Otherwise it is left out, with a warning showing the template |
| Users, groups, memberships | Users (same user name, e-mail, name) and groups. **Passwords are not carried over** (different hash scheme): users are created without a password and get an invitation or reset |
| `ownerships`, `shared_nodes` | Folder permission grants. Roles are sets of permissions: a role with node change permissions → Contribute, with delete → Edit, otherwise Read |
| Audit columns | Created and modified (time and user) of items and versions |
| Soft-deleted nodes | Skipped; `--include-deleted` puts them in the recycle bin |
| Thumbnails, SVG, hOCR, processing status | Not imported; regenerated here |

### Package format additions (general, not Papermerge-specific)

ADR-0028 leaves out several things the converter needs. They are added to the
package format so that PaperDotNet's own export benefits too:
1. **All file versions** in `doc:Files`, not only the current one.
2. **Page text** per file version. Files that have it skip OCR.
3. **Original timestamps and authors** of items and versions (only honored
   on create, and only for importers with the right to do this).
4. **Folder permission grants** by user or group name, in an optional
   section. It is applied only when the importer manages the workspace.

### Export to Papermerge: not planned

Writing Papermerge's database directly is rejected. It would need to:
- match the exact Alembic version;
- produce its polymorphic node rows, ownerships and UUID-based file paths;
- leave previews to its workers.

Any Papermerge upgrade could break it silently, and a mistake would corrupt
the target. If export is ever needed (PLT-16), it goes through Papermerge's
REST API: tags, custom fields, document types, folders, then uploads with
field values. It carries only what Papermerge can hold (documents, tags,
fields), and Papermerge OCRs the documents again.

## Consequences

- **Low barrier for Papermerge users:** archives move over with versions, OCR
  text, tags, types and fields, and without OCRing everything again.
- **Reuse:** the converter reuses package import; the format additions also
  make our own export/import more complete.
- **Contained dependency:** the Papermerge schema is confined to one tool with
  its own EF model, tested against fixtures per supported version.
- **Ongoing cost:** supporting a new Papermerge release is explicit work.
- **Large archives:** they produce large packages. The tool streams files,
  and it can split the output per owner (`--split-by-owner`) so each package
  stays under the 4 GB upload limit. The CLI import has no such limit.
- **Lossy parts are reported:** passwords, complex path templates and roles
  that don't map cleanly are listed as warnings in the dry run.

## As built (slice 7d)

The implementation follows the decision, with these differences:

- **Location and command:**
  - The converter is `src/Tools/PaperDotNet.Import.Papermerge`.
  - `paperdotnet import-papermerge --db --media --tenant --user` creates the
    users and imports the package. `--output` only writes the package.
- **Supported versions:** papermerge-core 3.6, Alembic revisions `a07f7fbbcca8`
  to `bb19aac50bca` (the head). Earlier versions are refused unless
  `--allow-unsupported-version` is given.
- **S3:** files are read from `media_root`; copy an S3 bucket to a folder
  first.
- **Layout: one workspace, not one per owner.**
  - The import creates one workspace (default `Papermerge`) with one library.
  - Each owner gets a top folder with unique permissions: the user's name
    (Manage), or `<group> (group)` (Contribute for the group).
  - The home folder becomes that folder, and the inbox becomes its `Inbox`
    sub-folder.
  - Users are workspace Visitors, so they only see their own folders and what
    is shared with them.
  - This keeps sharing between owners possible, which separate workspaces
    would not.
- **Sharing:**
  - A shared node gets unique permissions: the owner's grant, the shares
    inherited from above, and its own shares.
  - A role with any of `node.update`, `node.create`, `node.delete` or
    `node.move` gives Contribute; any other role gives Read.
- **Tags:**
  - Tags become terms of `Papermerge/Tags` with color and description, set in
    a multi-value `tags` field (managed metadata).
  - Tags with the same name are merged.
  - `/` in names becomes `-` (it separates term paths).
- **Path templates** are not converted: each is reported, so it can be set up
  as an automation with the `item.file` action.
- **Deleted nodes** are skipped. There is no `--include-deleted`, because
  packages do not carry recycle-bin state.
- **Package format additions:**
  - **Versions:** `doc:Files` entries have `versions` (file, name, source,
    created, createdBy, languages, textLanguage, pages, processed).
  - **Page texts:** stored as a JSON array in a package file. Versions with
    texts or marked `processed` are not processed again.
  - **Items:** entries carry `created`, `createdBy`, `modified`, `modifiedBy`
    (kept through `AuditOverrides`) and `permissions` (user or group names
    with levels). The item is created with its own permissions in the same
    save, so it is never visible with inherited ones.
  - **Who may keep stamps:** only callers who may apply packages
    (`template.manage`).
- **No `--split-by-owner`:** the CLI import has no size limit, so large
  archives use it.
