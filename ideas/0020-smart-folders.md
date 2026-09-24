# 0020: Smart folders (more than saved searches)

- **Status:** new
- **Area:** Platform / Navigation / Metadata
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

Virtual folders whose contents are defined by **rules on metadata**, not by
where items are stored. They go beyond a saved search:

1. **Rule-based membership across all data types:**
   - conditions on tags/terms (idea 0008), content type, fields, owner,
     dates, relations
   - relative values: "assigned to me", "due this week", "modified in the
     last 30 days"
   - example: "Project Apollo" shows the documents, tasks, events and notes
     tagged *Apollo*
2. **Two-way (drop to classify):** moving or creating an item *in* a smart
   folder **applies its rules** (adds the tag, sets the field values).
   Removing it reverses them. The folder works like a real folder, but it
   classifies the item.
3. **Dynamic sub-folders (metadata navigation):** a smart folder can group by
   fields to produce a virtual tree automatically. Examples:
   - *Invoices* → by *Year* → by *Counterparty*
   - *Tasks* → by *Status*
   - by taxonomy term hierarchy
4. **Manual includes/excludes:** pin an item into a folder, or hide one, on
   top of the rules.
5. **Folder behavior:**
   - smart folders can be nested inside normal folders and navigation
   - they have permissions and can be shared
   - they have their own default view (columns, sort, layout)
   - they can hold settings: a default content type for new items, and
     automation (e.g. "when an item enters this folder, create a review task",
     idea 0009)
   - subscribable (idea 0016) and exposed via WebDAV (idea 0018)

## Why / problem it solves

- Tags are the core of data management (idea 0008), but people think in
  folders. Smart folders give folder-style navigation over tags, with no
  single-location limit.
- Cross-type "project" or "customer" views without copying anything.
- Dropping into a folder is the fastest way to classify, easier than editing
  metadata.

## Examples / references

- SharePoint metadata navigation, views with group-by, managed navigation on
  term sets.
- macOS Finder smart folders / Gmail labels (an item can live in many).
- Papermerge: tags on folders and documents, but folders are physical only.
- Relates to "virtual folders" in idea 0008 and the views concept in
  [architecture-vision.md](../docs/architecture-vision.md) section 2.

## Notes

<!-- Open questions to settle during review:
     - Rule model: the same filter model as search/views (Papermerge
       operators: all/any/not, field operators), serialized as JSON. One
       query engine for search (idea 0013), views and smart folders.
     - Two-way rules only work for "settable" conditions (tag = X,
       field = Y). Relative or computed conditions (due this week) are
       read-only. How is that shown and validated?
     - Moving between two smart folders: remove the old folder's values,
       apply the new ones. Conflicts when rules overlap.
     - Applying values goes through normal validation and before-event
       handlers (idea 0012), so an extension can refuse.
     - Performance: evaluated as queries (indexed on tags/fields) rather than
       materialized membership. Group-by trees computed with facet counts.
     - Security: a smart folder only ever shows items the viewer may see;
       sharing a smart folder does not grant access to its items. Or should
       it (like sharing a real folder)?
     - Scope: personal, workspace-wide or tenant-wide smart folders; templates
       shipped by extensions (e.g. "My tasks", "Due this week", "Inbox to
       classify").
     - API: smart folders are addressable like folders
       (`/smartFolders/{id}/children`, drive-like in the Graph style, idea 0003)
       and available as MCP resources (idea 0004). -->
