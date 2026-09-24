# ADR-0015: Documents built on the SDK, content-addressed file storage

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Phase 3 adds the DMS (DOC-01…11, DOC-15). EXT-06 asks for the built-in apps to
be built on the public extension SDK, so the SDK is proven complete. Files need
storage that works with the minimal install (one container, one volume) and
can move to S3-compatible storage later.

## Decision

- **Documents is an SDK-only module** (`src/Modules/Documents`): a first-class
  module with its own routes, that references only
  `PaperDotNet.Extensions.Abstractions` (architecture test). Items, access and
  events come from the lists engine through `IListItemStore` and item events;
  its tables use `ExtensionDbContext` (tenant-owned only, audited, RLS) in the
  schema `documents`, with migrations in the host's migrations projects.
  Missing SDK pieces were added to the SDK, not worked around:
  `IListItemStore.GetListAsync`/`EnsureHomeAsync`, access levels on
  `ListData`/`ListItemData`, the `ItemPurged` event, `IBlobStore`.
- **A document is a library item plus file versions.** Item metadata stays in
  the lists engine (fields, item versions, permissions, search); the file side
  (`file_versions`) keeps every version forever (the original is never
  changed) until the item is purged.
- **Content-addressed storage:** `IBlobStore` (Abstractions) with keys
  `{tenant}/{sha[0..2]}/{sha256}`; `stored_files` holds one row per tenant and
  hash, so identical content is stored once (DOC-11) and exact duplicates are
  found by hash (DOC-10, policy per library: allow / warn / block; only items
  the caller can read are reported). Unreferenced content is removed by an
  hourly job after a one-hour grace period, so uploads in flight are safe.
- **Local disk first** (`{Storage:DataPath}/blobs`, atomic writes, strict key
  validation). The S3 provider comes when it can be tested against a real
  S3-compatible server.
- **Uploads** are multipart, streamed to a temporary file while hashing, with
  a configurable size limit (`Documents:MaxFileSize`, 100 MB). The type is
  detected from the first bytes (PDF, TIFF, JPEG, PNG); the file name only
  supplies the base name.

## Processing (3b)

- Processing runs as an operation per file version (`/v1.0/operations/{id}`,
  live events), automatically on new versions when the library wants it.
- PDFs with a usable text layer keep it (PdfPig). Images and scanned PDFs are
  OCRed with the **Tesseract CLI**: page images (PDFium at 300 dpi) go in as a
  list, a searchable PDF and plain text come out, and the PDF becomes a new
  file version (`source: ocr`); the original stays. No PDFsharp merge needed.
- Page texts are stored per content (`stored_file_pages`), so identical files
  share them; the lists engine asks Documents for the text of an item's current
  file when indexing it (`IItemSearchContributor`, SDK).
- Page images are rendered on demand (200/800/1600 px JPEG) and cached in the
  blob store by content hash; the thumbnail is rendered ahead of time.
- Live events are in-process (`ILiveEvents`, server-sent events); with several
  nodes a client sees events of its node only and should re-read state.

## Consequences

- The SDK grows with every built-in app; extensions get the same abilities.
- Deduplication is per tenant: no cross-tenant information leaks through
  duplicate detection or storage.
- File metadata is not part of item fields yet; item listings show the title,
  the Documents API shows the file. Processing (OCR, thumbnails) follows in 3b.
