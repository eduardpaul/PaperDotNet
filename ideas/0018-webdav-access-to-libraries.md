# 0018: WebDAV access to libraries

- **Status:** new
- **Area:** Integrations / Documents
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

Expose libraries and folders over **WebDAV**, so users can:

- mount them as a network drive in Windows Explorer, macOS Finder or Linux
  file managers
- open and save files directly from desktop apps
- sync with standard tools (rclone, WinSCP, Cyberduck, mobile file apps)

Saving a file creates a new version. New files go through the normal upload
pipeline (OCR, thumbnails, tagging rules).

## Why / problem it solves

- The most practical way to use the DMS from existing desktop tools and
  scripts, with no client to install.
- Gives a simple path for the Obsidian vault sync (idea 0007) and for bulk
  import/export.
- Works for self-hosters without extra services.

## Examples / references

- SharePoint "Open with Explorer" (WebDAV); Nextcloud WebDAV.
- Papermerge had no WebDAV; Paperless-ngx has none built in.

## Notes

<!-- Open questions to settle during review:
     - Implementation: a WebDAV endpoint inside the API (ASP.NET Core
       middleware). Evaluate existing .NET WebDAV server libraries, and check
       their license against the policy, or write a focused implementation
       (PROPFIND, GET, PUT, MKCOL, MOVE, COPY, DELETE, LOCK/UNLOCK).
     - Auth: app passwords/API tokens (clients don't support OIDC); per
       tenant URLs (idea 0005).
     - Mapping: workspaces/libraries/folders → directories; items → files;
       metadata/tags not visible (or as sidecar files?).
     - Non-file lists (tasks/events) are out of scope here, see CalDAV
       (idea 0019).
     - Locking and conflicts with concurrent edits; ETags.
     - Performance for large folders; streaming (System.IO.Pipelines).
     - Permissions are enforced exactly like the API; read-only mounts. -->
