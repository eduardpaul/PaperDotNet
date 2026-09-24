# 0006: Offline sync for mobile app

- **Status:** new
- **Area:** Mobile / Sync
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

A mobile app that works offline. Users can view, create and edit documents,
tasks, events and list items without a connection, and changes sync
automatically when the device is back online.

## Why / problem it solves

- Field work, travel and poor connectivity: users still need their tasks,
  calendar and key documents.
- Capture on the go: scan a document with the phone camera offline, upload later.

## Examples / references

- Microsoft Graph delta queries (`/delta`) for incremental sync (see idea 0003).
- OneDrive "available offline" files; Outlook and To Do mobile offline behavior.
- Architecture vision open decision 6: "out of scope for v1, keep the API
  sync-friendly (ETags, `modifiedSince`)".

## Notes

<!-- Open questions to settle during review:
     - Mobile stack: .NET MAUI (shares C# models), React Native (shares TS with
       the web UI), or Flutter?
     - Sync protocol: delta tokens per list plus ETags for optimistic concurrency.
       Soft deletes (tombstones) so deletions sync.
     - Conflict resolution: last-writer-wins per field, or conflicts shown to the
       user? Files: keep both versions (fits document versioning).
     - What is available offline: pinned lists/folders, "my tasks", calendar
       window (e.g. ±30 days), selected documents. Storage limits on device.
     - Local store: SQLite, encrypted at rest; remote wipe on logout.
     - Offline document capture: camera scan, edge detection, queued upload and OCR.
     - Permissions and multitenancy (idea 0005): revoked access must remove
       local data on next sync.
     - Extensions: how extension-defined content types and fields render and
       validate offline (schema sync, a mobile side of the extension SDK). -->
