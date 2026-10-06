# ADR-0047: WebDAV for libraries on a vendored Dav.AspNetCore.Server

- **Status:** Proposed
- **Date:** 2026-10-06
- **Plan:** [webdav-plan.md](../webdav-plan.md)

## Context

API-10 (idea 0018): members want to map document libraries as a network drive
in Windows Explorer and open files from desktop apps, with saves creating
versions. Windows Explorer talks to WebDAV servers through the WebClient
service (the "Mini-Redirector"). It has some hard limits:

- **Authentication:** only Basic, Digest or NTLM/Kerberos; no OAuth. Basic is
  sent only over HTTPS unless the `BasicAuthLevel` registry value is changed.
- **Protocol:** it needs `OPTIONS` (with `DAV` and `MS-Author-Via` headers),
  `PROPFIND` depth 0/1 and `GET`. To write, it also needs `PUT`, `MKCOL`,
  `MOVE`, `DELETE`, `PROPPATCH` (Win32 attributes) and `LOCK`/`UNLOCK`, which
  Office requires before it opens a file read-write.
- **Downloads** are capped at 50 MB by default (`FileSizeLimitInBytes`).

[ADR-0017](0017-phase-5-scope.md) moved WebDAV to P7 because there was no
mature MIT/Apache WebDAV server library for .NET, and CLAUDE.md says we never
hand-roll protocol code. We looked at the options again:

| Option | License | State |
|---|---|---|
| [Dav.AspNetCore.Server](https://github.com/ThuCommix/Dav.AspNetCore.Server) | MIT, no dependencies | RFC 4918 class 1 and 2, ASP.NET Core middleware, pluggable `IStore`, `ILockManager` and `IPropertyStore`. Small (about 5,500 lines with tests), one maintainer, targets net7.0, last commit 2025-10-10 (`7ef7cbf`) |
| NWebDav.Server | MIT | Older design, 0.2 still in beta (net7.0) |
| FubarDevelopment WebDavServer | MIT | Unmaintained since 2019 |
| IT Hit WebDAV Server Engine | Commercial | Not allowed |
| Our own implementation | n/a | Protocol code we would have to own entirely |

## Decision

1. **We vendor Dav.AspNetCore.Server.** Its source, pinned at commit
   `7ef7cbf80c5feb38556dd55f968cc1248955c55d`, is copied into
   `src/BuildingBlocks/PaperDotNet.WebDav`.
   - The MIT license and copyright notice stay with the code. The project's
     README records the upstream commit and lists our changes.
   - It targets net10.0 and builds under our rules (warnings as errors,
     `dotnet format`).
   - We remove what we don't use: Digest and the library's own Basic handler,
     the local-file store, the XML-file property store and the SQL extensions.
   - The upstream unit tests are ported to xunit v3.
   - Fixes go to upstream as pull requests when they are generally useful.
   - Only the Dav module may reference this project (architecture test).
2. **A `Dav` module built on the SDK.** `src/Modules/Dav` maps `/dav` and
   implements the library's `IStore` using `IListItemStore`, `IWorkspaceAccess`
   and Documents contracts only, like Documents itself
   ([ADR-0015](0015-documents-on-the-sdk.md)). Permissions, versions, events
   and the recycle bin therefore behave exactly as they do in the REST API. It
   never references module implementations.
3. **Namespace:** `/dav/{workspace}/{library}/{folder…}/{file}`. Tokens carry
   the tenant, so the URL needs no tenant segment.
   - The root lists the workspaces the caller can read, Home included. A
     workspace lists only its document libraries. Below that come folders and
     files.
   - A file's name is its item title plus the extension of its current file.
     A folder's name is its title.
   - Names are made safe for Windows: `\ / : * ? " < > |` and control
     characters become `_`, trailing dots and spaces are trimmed, reserved
     names (`CON`, `NUL`, …) get a `_` suffix, and names are cut to 255
     characters.
   - When siblings have the same name (case-insensitive), all but the first
     get ` (2)`, ` (3)` … before the extension. Order is by item id (UUIDv7,
     so creation order), so existing names stay stable when new duplicates
     appear.
   - Items without a file are not shown.
4. **Authentication:** HTTP Basic, with an API token (`pdn_…`) as the
   password.
   - The API token handler also accepts `Authorization: Basic`, but only on
     requests under `/dav`. The user name is ignored, because the token
     identifies the user.
   - Basic credentials are ambient, like cookies, so the rest of the API
     never accepts them. This also keeps [ADR-0045](0045-generic-reverse-proxies.md)'s
     separation from OAuth endpoints.
   - A 401 under `/dav` answers `WWW-Authenticate: Basic realm="PaperDotNet"`,
     so clients prompt for credentials.
   - The tenant is resolved through the existing API-token claim strategy.
   - Scopes: reading needs `list.read` and `document.read`; writing needs
     `list.write` and `document.write`. There is no new scope.
5. **Read-only first.** Slice 1 advertises `DAV: 1` (no class 2, so Office
   opens files read-only) and serves `OPTIONS`, `PROPFIND` (depth 0/1;
   infinity is refused with `propfind-finite-depth`), `GET` and `HEAD`, with
   ranges and ETags. Every other method answers 403.
6. **Libraries accept any file type** (a change to DOC-02), as a prerequisite
   for writes.
   - Detection by content stays for the types we process. Other files are
     stored as uploaded, with the media type taken from the file name's
     extension, or `application/octet-stream`.
   - Built-in workflows skip media types they cannot handle.
   - The size limit and the duplicate policy apply as before.
   - This applies to every upload path (REST, MCP, CLI, packages), not only
     WebDAV.
7. **Writes come later** and use the same pipeline:
   - `PUT` on a new name uploads a document; `PUT` on an existing file adds a
     file version, which raises `document.added`.
   - `MKCOL` creates a folder.
   - `MOVE` renames (the title changes) or moves an item within its list or
     to another library. Moving to another library preserves identity.
   - `DELETE` sends the item to the recycle bin.
   - Locks are kept in the `dav` schema through EF Core, on both providers,
     as tenant-owned rows. Win32 properties are accepted but not stored.
   - Temporary files of Office and Explorer (`~$*`, `*.tmp`, `desktop.ini`,
     `Thumbs.db`, `._*`, `.DS_Store`) never become items.
   - Details are in the plan.
8. **HTTPS is required.** Documentation shows the reverse-proxy setups we
   already have. It also describes the `BasicAuthLevel` registry change for
   tests on a LAN only (with a warning), and the `FileSizeLimitInBytes`
   change for larger downloads.

## Consequences

- We own a small protocol library (about 3,000 lines once trimmed). That is
  less than writing our own, and its tests come with it. We take on its
  maintenance.
- WebDAV adds no tables until locks arrive. It adds two Documents contracts:
  bulk file metadata, and later uploads and replacements.
- Allowing any file type changes behaviour for every client. Libraries that
  want only scans can use a workflow or a later per-library setting.
- Explorer cannot page, so very large folders are listed up to
  `WebDav:MaxFolderEntries` (default 5,000). Entries beyond that are left out
  and a warning is logged.
- `WebDav:Enabled` (default `true`) turns the endpoint off for an
  installation.
