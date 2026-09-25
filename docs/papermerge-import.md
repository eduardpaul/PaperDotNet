# Importing from Papermerge

`paperdotnet import-papermerge` (PLT-15) moves a Papermerge 3.6 archive to
PaperDotNet:
- folders;
- documents with **every file version and their OCR text**, so nothing is
  OCRed again;
- tags, document types and custom fields;
- users, groups and sharing.

Design: [ADR-0029](adr/0029-papermerge-import.md).

```bash
# Papermerge's database (read-only access is enough) and its media_root (with docvers/).
paperdotnet import-papermerge \
  --db "Host=pm-db;Database=papermerge;Username=papermerge;Password=…" \
  --media /srv/papermerge/media \
  --tenant default --user admin --dry-run      # then again without --dry-run
```

**Options:**
- `--workspace` and `--library` name what is created (default `Papermerge` /
  `Documents`).
- `--output archive.zip` only writes the package. Create the users, then run
  `paperdotnet import archive.zip --tenant … --user …`.

**Order of work:**
1. **Checks the version.** Only papermerge-core 3.6 schemas are read (Alembic
   `a07f7fbbcca8` to `bb19aac50bca`); others are refused unless
   `--allow-unsupported-version` is given.
2. **Converts** the database and files into a normal package (template, items,
   files).
3. **Creates the users** that are missing, **without passwords** (Papermerge's
   password hashes cannot be carried over). Reset their passwords
   (`POST /v1.0/users/{id}/password`) or let them sign in through your
   identity provider or reverse proxy.
4. **Imports** the package like `paperdotnet import`. Running it again
   creates nothing twice.

## What goes where

| Papermerge | PaperDotNet |
|---|---|
| Home folder of a user | Folder named after the user in the library, with unique permissions (the user: Manage) |
| Home folder of a group | Folder `<group> (group)` (the group: Contribute) |
| Inbox | Folder `Inbox` inside the owner's folder |
| Folders and documents | Folders and documents below, with their titles, original creation and change times and authors |
| Document versions | File versions in order; `creation_reason` becomes the version's source (`upload`, `page_edit`, …); page texts and language (`lang`) are kept |
| Tags | Terms of `Papermerge/Tags` (color, description) in a `tags` field; the same name from different owners becomes one term; `/` becomes `-` |
| Document types | Content types with a `tags` field and the type's custom fields in order |
| Custom fields | short_text → text, text → note, integer / number → number, monetary → currency (the configured currency, default EUR), date → date, datetime → dateTime, select → choice, multiselect → multiple choice, boolean, url, email, yearmonth → text |
| Groups | Groups with their members |
| Sharing | Unique permissions on the shared folder or document: roles that may change nodes give Contribute, others Read |
| Superusers | Reported. Assign the Administrator role yourself |

Users are Visitors of the workspace, so each sees only their own folders and
what was shared with them.

**Not carried over** (reported where it applies):
- **Path templates:** set them up as automations with the `item.file` action.
- **Deleted nodes and inactive users.**
- **Missing files:** files that are missing in `media_root`. For S3, copy the
  bucket to a folder first.
- **Previews:** thumbnails and previews are generated again.
