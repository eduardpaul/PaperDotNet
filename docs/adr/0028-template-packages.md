# ADR-0028: Template packages for content (and export/import)

- **Status:** Accepted; extended by [ADR-0029](0029-papermerge-import.md) (all file versions, page texts, stamps, item permissions)
- **Date:** 2026-09-25

## Context

Templates (ADR-0017) carry configuration as XML and refer to objects by name.
Two features need content as well:
- **PRV-04:** ship a solution with sample or reference items and documents.
- **PLT-13:** move a workspace or tenant, with its data, in an open format.

Content brings ids (terms, users, lookups, folders), binary files of any size,
and the need to apply the same package more than once without creating
duplicates.

## Decision

- **One format for both: a zip package.** It holds:
  - `template.xml`, the template unchanged, with content sections;
  - JSON documents under `content/`;
  - file contents under `files/<sha256>`, each stored once.

  `ITemplatePackage` (Provisioning.Contracts) gives sections access to it on
  export and apply. A plain XML template has no package, and content sections
  refuse to run without one. Entries are only looked up by name, never
  extracted to disk. JSON and XML entries are limited in size; files are
  streamed.
- **Sections own their content**, like configuration:
  - `Items` (Lists, template namespace, in the XSD) holds items and folders,
    parents first, with portable values: terms as paths, keywords as text,
    users as user names, lookups as list path + key.
  - `doc:Files` (Documents) maps item keys to files; files are checked and
    processed like uploads.
- **Idempotent, additive apply.** An item created from key `k` in list `L` gets
  the id `TemplateContent.ItemId(L, k)`, a name-based UUID. Applying again finds
  it and creates nothing. Existing items are never changed or deleted (content
  is seed data, not a sync).
- **Normal write path.** Items go through `ItemWriter`, so they are validated,
  mutated, versioned, indexed and seen by automations. Lookups are set in a
  deferred pass once every list is filled, and are recognised by their form,
  because a lookup field itself may only be completed then.
- **What is left out:** file versions other than the current one, item history,
  comments, item-level permissions and personal workspaces. Unknown users or
  terms are left out with warnings.

## Consequences

- The same machinery serves templates with content and export/import (PLT-13);
  extensions add content sections the same way (PRV-05).
- A package can be read and produced by other tools: zip, XML with an XSD, and
  JSON.
- Deriving ids from keys makes applies repeatable but ties items to their
  target list. A re-import into a different list creates new items.
