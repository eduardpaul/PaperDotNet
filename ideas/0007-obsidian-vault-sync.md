# 0007: Obsidian vault sync

- **Status:** new
- **Area:** Integrations / Sync
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

Sync an Obsidian vault (a folder of Markdown notes with attachments) with the
app. Notes, folders and attachments show up as items or documents in the app,
and changes made on either side flow back to the other.

## Why / problem it solves

- Many people keep their knowledge base in Obsidian. Syncing puts notes next
  to documents, tasks and events, so they are searchable and linkable together.
- Obsidian notes often contain tasks (`- [ ]`) and dates. These could map to
  real tasks and calendar items.

## Examples / references

- Obsidian vault = a plain folder: `.md` files with YAML frontmatter,
  `[[wikilinks]]`, `#tags`, embedded attachments.
- Obsidian Sync, Remotely Save and obsidian-git plugins; Obsidian Tasks and
  Dataview plugins (task and metadata conventions).

## Notes

<!-- Open questions to settle during review:
     - Direction: one-way import, one-way export (app → vault), or two-way sync?
     - Mechanism: an Obsidian community plugin (TypeScript) that talks to our API
       / generated JS SDK (idea 0003), or a desktop/CLI sync agent watching the
       folder, or WebDAV exposed by the app.
     - Mapping:
         - Note = item in a "Notes" list/library (new content type), with the
           Markdown body as content
         - frontmatter = fields
         - #tags = taxonomy tags
         - [[wikilinks]] = lookup/relation links
         - attachments = documents (OCR, search)
         - checkbox tasks = Task items (Obsidian Tasks syntax: due 📅, priority)?
     - Conflict handling: same questions as offline sync (idea 0006), e.g. keep
       both versions and flag the conflict.
     - Should "Notes" become a first-party extension (a Markdown content type
       plus an editor), alongside Documents, Tasks and Calendar?
     - Multitenancy/permissions (idea 0005): which vault maps to which workspace. -->
