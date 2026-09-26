# Smart folders and taxonomy curation

Phase 5f: navigate by meaning instead of location (TAX-08…10), and keep the
vocabulary healthy (TAX-05, TAX-11). Design:
[ADR-0022](adr/0022-smart-folders.md).

## Smart folders (TAX-08)

A smart folder is a saved, rule-based view over the items of many lists:
documents, tasks, events and any other list.

- **Personal** folders (`"personal": true`) are only visible to their owner.
  They cover all the owner's workspaces, or the one in `workspaceId`.
- **Shared** folders belong to a workspace. Everyone who can read the workspace
  sees them; managers change them. They travel with workspace templates.

```http
POST /v1.0/smartFolders
{
  "name": "Project Apollo",
  "workspaceId": "…",
  "definition": {
    "terms": ["<Apollo term id>"],
    "listTemplates": ["tasks", "documents"],
    "filter": "fields/status ne 'done'",
    "groupBy": [ { "field": "dueDate", "by": "month" } ]
  }
}
```

**Definition** (all parts optional, combined with "and"):

| Part | Meaning |
|---|---|
| `lists` | List names |
| `listTemplates` | List kinds, e.g. `tasks`, `events` |
| `contentTypes` | Content type names or keys |
| `terms`, `termMatch` | Term ids. `all` (default) or `any`. Each term also matches its child terms, in every managed metadata or keywords field that can hold it |
| `filter` | OData filter over fields, as in the items API. Lists without these fields are left out |
| `groupBy` | Up to 3 levels of virtual sub-folders (TAX-10) |
| `includeFolders` | Also show folders (default: no) |

**Relative values** work in folder filters and in every item query or view:

- `@me` (the current user) and `@now`;
- `@today`, `@yesterday`, `@tomorrow`;
- `@weekStart` and `@weekEnd` (Monday to Sunday), `@monthStart` and `@monthEnd`;
- `@last7Days`, `@next7Days`, `@last30Days`, `@next30Days`.

Dates are in UTC. Example:
`fields/assignedTo eq @me and fields/dueDate le @next7Days`.

**Reading:**

- `GET /v1.0/smartFolders` lists the caller's folders; `GET`, `PATCH` (with
  `If-Match`) and `DELETE /v1.0/smartFolders/{id}` manage one.
- `GET /v1.0/smartFolders/{id}/items` returns items of all matching lists, most
  recently changed first (`$top`, `$skiptoken`). Each entry has `workspaceId`,
  `listName` and the `item`.
- Only items the caller can read appear. Sharing a folder does not grant access
  to items.
- A folder covers up to 100 lists.

## Metadata navigation (TAX-10)

`groupBy` turns a folder into a virtual tree, e.g. *Invoices → Year →
Counterparty*:

```json
"groupBy": [ { "field": "invoiceDate", "by": "year" }, { "field": "counterparty" } ]
```

- `GET …/groups?path=2026` returns the next level with counts:
  `{ "value": [ { "value": "ACME", "label": "ACME", "count": 12 }, … ] }`.
  - Labels are term names for managed metadata fields and user names for person
    fields.
  - Items without a value form the `(empty)` group (`value: null`; use an empty
    `path=` value for it).
- `GET …/items?path=2026&path=ACME` lists the items of a sub-folder.
- Group fields must be single-value text, choice, date, date-time, person,
  lookup or managed metadata fields. `by` (`year`, `month`) applies to date
  fields.

## Drop to classify (TAX-09)

Putting an item into a smart folder applies the folder's rules:

- `POST /v1.0/smartFolders/{id}/items` with
  `{ "workspaceId", "listId", "itemId" }` classifies an existing item.
- With `{ "workspaceId", "listId", "fields" }` instead, it creates a new item.
- Add `"path": [...]` to drop into a sub-folder.

What gets set:

- **Terms:** the folder's terms go into the matching term fields. Multi-value
  fields keep their other terms.
- **`eq` conditions:** each `fields/x eq value` condition that is joined with
  `and` in the filter sets `x` (`@me` becomes the current user).
- **Sub-folder values:** the values of plain `groupBy` levels are set. Computed
  levels such as the year are not.

`DELETE /v1.0/smartFolders/{id}/items/{itemId}?workspaceId=&listId=` takes the
item out: it removes the terms and clears the fields that hold the folder's
values.

All changes go through the normal item pipeline: validation, permissions,
mutators, versions, events and automation. The list must be one the folder
covers.

## Showing term values

Managed metadata and keyword fields store term ids. `GET /v1.0/termStore/terms?ids=a,b,c`
(at most 200, any term set) returns those terms with their names, labels and colors, so a
client can show a page of items with one request. Unknown ids are left out.

## Promoting keywords (TAX-05)

- `GET /v1.0/termStore/keywords/popular?top=50` returns keywords by the number
  of items tagged with them (taken from the search index).
- `POST /v1.0/termStore/keywords/{id}/promote` with
  `{ "termSetId", "parentId"? }` promotes a keyword:
  - **Moved:** the keyword moves into the term set with the same id, so tagged
    items stay valid.
  - **Merged:** if a term with that name already exists there, the keyword is
    merged into it and items are rewritten.
  - Either way, the term stays usable in keywords fields (typing its name
    finds it).

## Importing term sets and extension term sets (TAX-11)

- **CSV import:** `POST /v1.0/termStore/groups/{groupId}/import` with a
  `text/csv` body in the SharePoint term set format (up to 1 MB).
  - Columns: `Term Set Name, Term Set Description, LCID, Available for Tagging,
    Term Description, Level 1 Term … Level 7 Term`.
  - The first data row names the set.
  - The import is additive: importing again adds only missing terms.
- **Extensions:** `builder.AddTermSet(new TermSetTemplate(group, name, terms, …) { Key = "{extension id}.…" })`.
  - The term set is created when a tenant enables the extension.
  - Later versions add missing terms and synonyms but never remove any.
