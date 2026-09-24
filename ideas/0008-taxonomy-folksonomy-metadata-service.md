# 0008: Taxonomy and folksonomy (managed metadata service)

- **Status:** new
- **Area:** Platform / Metadata
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

A central metadata service, similar to SharePoint's Managed Metadata Service
(term store). It should support both:

- **Taxonomy:** curated, hierarchical, admin-managed terms. Examples:
  Department > Finance > Accounts Payable; Document category; Project; Customer.
- **Folksonomy:** free, user-created tags added on the fly (like SharePoint
  Enterprise Keywords). Popular folksonomy tags can later be promoted into the
  taxonomy.

**Tags are the core of data management.** They are a first-class part of the
core platform, not just a field type. Every item type (documents, tasks,
events, notes, extension content types) is tagged the same way, and tags drive
organization, navigation, search, views, permissions and automation.

## Why / problem it solves

- Folders force a single place per item. Tags allow many classifications at once
  and work across lists, libraries and data types.
- Shared, governed vocabularies give consistent metadata across the whole
  tenant. That enables reliable search, filtering and reporting.
- Folksonomy keeps tagging fast and low-friction for everyday users.

## Examples / references

- SharePoint Managed Metadata / term store: term groups, term sets, terms;
  hierarchy; synonyms and multilingual labels; open vs closed term sets;
  deprecate/merge/move/reuse/pin terms; managed metadata columns; Enterprise
  Keywords; managed navigation.
- Papermerge: flat, colored tags on documents and folders
  (see [papermerge-features.md](../docs/papermerge-features.md) section 4).
- Architecture vision: the "Taxonomy" concept in section 2 and the
  "Lists engine" phase 1 in section 5.

## Notes

<!-- Open questions to settle during review:
     - Term store structure: tenant-level store, then term groups (with group
       managers), then term sets (open or closed), then hierarchical terms.
       Workspace-local term sets as well?
     - Term features:
         - stable IDs
         - multilingual labels and synonyms
         - descriptions, colors and icons (keep Papermerge-style colored tags)
         - custom properties
         - deprecate, merge (rewrites usages), move, reuse, pin
     - Folksonomy:
         - one global "keywords" set per tenant, or per workspace
         - moderation
         - promotion to the taxonomy
         - suggestions and autocomplete
     - Field types: "Managed metadata" (bound to a term set, single or multi
       value) and "Keywords" (folksonomy). Tagging items with any term on any
       content type.
     - Storage and query:
         - item-term join table, plus materialized ancestor paths so
           "tagged Finance" matches all child terms
         - fast faceting
         - usage counts
     - Tag-driven features:
         - faceted search and filters (Papermerge operators: all / any / not)
         - tag-based views and "virtual folders" navigation
         - automation triggers ("when tagged X")
         - tag-based permissions or sharing?
         - auto-tagging (rules, OCR text, AI classification)
     - Extensions: extensions can ship term sets and hook into auto-tagging.
       Available via API (idea 0003) and MCP (idea 0004).
     - Multitenancy (idea 0005): the term store is per tenant; import/export
       (CSV, SKOS). -->
