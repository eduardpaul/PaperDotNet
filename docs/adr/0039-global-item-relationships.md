# ADR-0039: Stable item identities and global relationships

Status: Accepted (implemented)

Date: 2026-10-02

Content items already share a GUID primary key in the Lists engine. Their identity must remain stable across lists
and workspaces so that documents, tasks, notes, events and custom content types can remain connected.

Reuse `ListItem.Id`; do not add a parallel global identifier or expose internal database rows as content. Generated
identifiers remain UUIDv7. Global lookup is tenant-scoped and checks the current workspace, list and item permissions.

Store a symmetric relationship once, in `lists.item_relations`, using a canonical ordered pair of item ids. Enforce
pair uniqueness and foreign keys to both items. The universal Related UI supports multiple relationships without
duplicating arrays in either item's JSON or adding fields to extension-owned content type definitions. Existing
list-constrained lookup fields, directed task dependencies and title-based note wiki links retain their semantics.

Creating or removing a relationship requires Contribute on both endpoints. Listing trims inaccessible and recycled
endpoints before paging. Links do not grant access, cross tenants, or disclose inaccessible titles or counts.
Recycling retains links for restore; permanent deletion cascades their removal. Copies receive their own identity
and no implicit relationships.

Global item resolution and permanent `/i/{id}` UI URLs find the current location. Cross-list moves retain the original
row, fields, audit creation stamps, versions and relationships. Destinations must have the same list kind and support
the existing content type. Folder trees can still move within a list; moving folder trees across lists is excluded.
The moved item inherits destination folder/list permissions, and source unique grants are removed. Moves require
the item's ETag and Contribute on both the source item and destination folder/list.

Module-owned location metadata is changed synchronously through `IItemMoveParticipant` using a shared database
transaction. The outbox commits the item change and integration event in that transaction. A participant failure
rolls everything back. Rebuild per-list field slots/value rows and record source removal plus destination upsert for
delta sync. Search follows the saved update asynchronously.

Packages serialize relations using list paths and item keys, then resolve them after all items are imported. Workspace
exports omit relationships outside that workspace with a warning. Imports remap endpoints to destination identities;
missing targets are warned about and skipped. Reapplying a package is additive and does not duplicate relationships.

[ADR-0040](0040-typed-item-relationships.md) extends this model with taxonomy-backed predicates, direction, endpoint
limits and graph-aware workflow scripts. The symmetric, untyped behavior described here remains the default.
