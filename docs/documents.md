# Documents: pages, languages and inboxes

This guide covers what slice 7f added to document libraries:
- page operations (DOC-05, DOC-06);
- languages per file (DOC-17);
- group inboxes (DOC-16);
- values on folders (LST-19).

Uploads, versions and processing are described in [features.md](features.md)
(phase 3).

## Page operations

Page operations work on the current file when it is a PDF. Images get a PDF
when they are processed. Every change is **a new file version**, so the
earlier versions stay and can be restored.

**Page texts are carried over:** search keeps finding the pages, with page
hits, and nothing is OCRed again. You need Contribute on every document
involved.

| Request | What it does |
|---|---|
| `PUT …/items/{id}/file/pages` `{ "pages": [ { "page": 3 }, { "page": 1, "rotate": 90 } ] }` | The new file has these pages in this order. Pages left out are deleted; `rotate` turns a page clockwise (90, 180, 270). One request can delete, reorder and rotate pages together (DOC-05). An optional `If-Match` takes the file's ETag. |
| `POST …/file/pages/extract` `{ "pages": [2, 3], "separate": false, "remove": true, "workspaceId", "listId", "folderId", "title" }` | Creates a new document from the pages, or one per page with `separate`. By default it goes into the same library and folder. `remove` also deletes the pages from the source (DOC-06). |
| `POST …/file/pages/move` `{ "targetWorkspaceId", "targetListId", "targetItemId", "pages": [1], "position": "append" }` | Moves pages into another document: `append`, `prepend`, or `replace` its pages. Without `pages`, all pages move. A source left without pages goes to the recycle bin, so moving all pages **merges** two documents. |

**Responses:**
- The edit returns the new file version.
- Extract and move return `{ "source", "sourceDeleted", "documents", "target" }`.

**Errors:**
- `409 pdfRequired`: the current file is not a PDF.
- `409 pdfNotEditable`: the PDF is encrypted or damaged.

## Languages per file

Uploads (`…/documents`, `/v1.0/me/inbox/documents`, group inboxes) and file
replacements accept a form field `languages`: Tesseract codes such as
`fra+eng`.

- **New versions** keep the file's languages.
- **`POST …/file/process`** with `languages` processes the file again and
  keeps the new languages.
- **Versions** show `languages` (chosen) and `textLanguage` (used for search
  stemming).

**Order used for OCR:**
1. the process request;
2. the file;
3. the library (`…/documentSettings`);
4. the uploader's document languages (`/v1.0/me/preferences`);
5. the organization's default.

## Group inboxes

Any library can be a group's inbox. Members of the group upload into it and
find it next to their personal inbox.

| Request | What it does |
|---|---|
| `PUT /v1.0/groups/{groupId}/inbox` `{ "workspaceId", "listId" }` | Makes the library the group's inbox. Needs Manage on the library, and on the previous inbox library if there was one. |
| `GET` / `DELETE /v1.0/groups/{groupId}/inbox` | Reads or removes the designation |
| `POST /v1.0/groups/{groupId}/inbox/documents` | Uploads a file (`file`, `title`, `languages`). Only members of the group can do this, and they need Contribute on the library like any upload. |
| `GET /v1.0/me/inboxes` | Lists the caller's personal inbox and the inboxes of their groups that they can read |

**Access:** designating a library does not change its permissions. Give the
group Contribute on the library, or on its workspace. A scanner can upload
with an API token of a user in the group.

**Portability:** the designation travels in templates as
`GroupInbox="Group name"` on the library's `doc:LibrarySettings` section.
Deleting the group removes it.

## Values on folders

Folders can hold values of their content type's fields, for example keywords
or terms, so that they can be tagged like documents.

- **Validation:** values are checked as usual, but required fields and
  default values only apply to items.
- **Filters and templates:** `$filter` works on folder values, and folder
  values are exported with their items in template packages.
