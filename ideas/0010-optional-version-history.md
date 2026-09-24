# 0010: Optional (opt-in) version history for data

- **Status:** new
- **Area:** Platform / Data
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

Version history should be optional and configurable per list, library or
content type, not always on. Each list decides whether changes to its items
(field values and, for libraries, files) are kept as versions, and how many.

## Why / problem it solves

- Not all data needs history. High-volume or throwaway lists (logs, imports,
  checklist items) would waste storage and slow writes.
- Some data must keep full history (contracts, documents, approvals) for
  audit and compliance.
- Admins get control over storage cost per tenant (idea 0005).

## Examples / references

- SharePoint list/library versioning settings:
  - no versioning, major versions only, or major and minor (drafts)
  - limit on the number of versions kept
  - require check-out
  - draft item security
- Papermerge: every page operation creates a document version, so the
  original is always kept (see [papermerge-features.md](../docs/papermerge-features.md)
  section 1).
- Architecture vision section 4, "Versioning at item level". Today it says
  every item keeps field-value history. This idea makes that configurable.

## Notes

<!-- Open questions to settle during review:
     - Settings per list/library (inherit defaults from content type or
       workspace):
         - off, or major only, or major + minor
         - max versions to keep (count and/or age)
         - separate settings for field history vs file versions?
     - Documents: should file versioning always stay on (the non-destructive
       page operations need it), with only field history optional?
     - What turning versioning off means for existing history: keep, trim, or
       delete (as a background job)?
     - Storage model:
         - item_versions table holding a snapshot or a diff of the JSON field
           values, stored via EF Core (provider-agnostic)
         - or temporal tables (a provider-specific feature, so it would sit
           behind an abstraction)
     - Relation to the audit log: the audit log records who/what/when always.
       Version history stores restorable content only when enabled.
     - API: list versions, get version, compare, restore
       (Graph-style `/items/{id}/versions`, idea 0003).
     - Retention and legal hold: can a retention policy force versioning on
       and block trimming? (automation, idea 0009)
     - Extensions can declare a default versioning policy for their content
       types. -->
