# Notes

Notes are Markdown list items (LST-18) in lists created from the `notes`
template (content type `note`: `title`, `body`, `tags`). They sit next to
documents, tasks and events: they are searched, versioned and permissioned like
any item. The Notes module adds the following, following Obsidian's
conventions.

## #tags

`#tags` in the body are added to the note's `tags` (keywords) whenever the body
changes.
- **Allowed characters:** letters, digits, `_`, `-` and `/` for nesting
  (`#project/alpha`). A tag of only digits (`#123`) is not a tag.
- **Code:** tags in code spans and fenced code blocks are ignored.
- **Removing tags:**
  - Removing a `#tag` from the body keeps the keyword; remove it from `tags`.
  - A keyword removed from `tags` by hand stays removed until the body changes
    again.

## [[Wiki links]]

`[[Title]]`, `[[Title#Heading]]`, `[[Title|shown text]]` and `![[Title]]`
(embed) link to the note with that title in the same workspace.
- **Matching:** titles match case-insensitively. For `Folder/Note.md` only
  `Note` counts. When several notes share a title, the oldest wins.
- **Links before their note exists:** a link to a title no note has yet waits.
  It resolves as soon as a note gets that title.
- **Renames:** when a note is renamed, the notes that link to it are rewritten
  to the new title, keeping headings and aliases. Code is left alone.
- **Deletes:** when a note is deleted, links to it wait again.
- **Timing:** links are updated in the background, a moment after a save.

| Request | Returns |
|---|---|
| `GET …/lists/{list}/items/{id}/noteLinks` | The note's links in order: `target`, `heading`, `alias`, `embed`, and `note` (the linked note, when it exists and you can read it) |
| `GET …/lists/{list}/items/{id}/backlinks` | Notes that link to this one and that you can read |

Both need Read access to the note (scope `note.read`).
