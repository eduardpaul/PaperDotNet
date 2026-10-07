# ADR-0047: WebDAV for libraries on a vendored FubarDev.WebDavServer

- **Status:** Proposed
- **Date:** 2026-10-06
- **Plan:** [webdav-plan.md](../webdav-plan.md)
- **Evidence:** [spikes/webdav-poc](../../spikes/webdav-poc/README.md) and
  [spikes/webdav-poc-fubar](../../spikes/webdav-poc-fubar/README.md)

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
| [FubarDevelopment/WebDavServer](https://github.com/FubarDevelopment/WebDavServer), branch `release/2.0` | MIT | RFC 4918 class 1 and 2 with a virtual file system (`IFileSystemFactory` per user, `ICollection`, `IDocument`), lock manager base class, property stores. About 28,000 lines plus 282 unit tests. `release/2.0` (200 commits after `master`, last commit 2022-11-24, `1f78db5`) was never released. |
| [Dav.AspNetCore.Server](https://github.com/ThuCommix/Dav.AspNetCore.Server) | MIT | Small (about 3,000 lines), plain middleware, but MOVE is copy + delete and PUT drops store errors; it needed 15 protocol fixes in the spike |
| NWebDav.Server | MIT | Older design, 0.2 still in beta (net7.0) |
| IT Hit WebDAV Server Engine | Commercial | Not allowed |
| Our own implementation | n/a | Protocol code we would have to own entirely |

The spikes ran both open-source candidates against the same model, the same
Explorer-like request sequences and litmus. Unchanged, Fubar passed litmus
basic 16/16, copymove 13/13, locks 38/41 and http 4/4, and 73 of 76
read-write checks. Dav.AspNetCore.Server reached that level only with 15
fixes, two of them blocking.

## Decision

1. **We vendor FubarDev.WebDavServer `release/2.0`.** Its core and models,
   pinned at commit `1f78db5d002793092d6fef4eda91006a2d56ab86`, are copied into
   `src/BuildingBlocks/PaperDotNet.WebDav` (one project, the upstream
   namespaces kept so that upstream diffs stay readable).
   - The MIT license and copyright notice stay with the code. The project's
     README records the upstream commit and lists our changes.
   - It targets net10.0 only and builds in the solution. The vendored files
     are marked as generated code in `.editorconfig`, so our analyzers and
     `dotnet format` leave their upstream style alone; new files follow our
     rules.
   - We drop what we don't use or can't keep: the SQLite/TextFile/DotNet
     stores, the Digest handler, the user-agent parser, Scrutor (explicit
     registrations instead), System.Interactive.Async (in .NET 10), and the
     Yoakke parser generator (six nightly packages for one header grammar,
     replaced by a small parser for the `If`, entity-tag and `Lock-Token`
     headers covered by the upstream tests).
   - The ASP.NET Core layer (an MVC controller) is replaced by one Minimal API
     endpoint that calls the library's dispatcher, so the host needs no MVC.
   - Our fixes: folder MOVE through the file system (identity kept), the
     `Timeout` header with spaces, `Content-Length` on HEAD, PROPPATCH without
     a body, malformed conditional headers.
   - The upstream unit tests that test the library itself (headers, entity
     tags, URI comparison, converters, lock manager) are ported to xunit v3.
     Protocol behaviour is covered by our integration tests and litmus.
   - Only the Dav module may reference this project (architecture test).
2. **A `Dav` module built on the SDK.** `src/Modules/Dav` maps `/dav` and
   implements the library's file system (`IFileSystemFactory` per user) with
   `IListItemStore`, `IWorkspaceAccess` and Documents contracts only, like
   Documents itself ([ADR-0015](0015-documents-on-the-sdk.md)). Permissions,
   versions, events and the recycle bin therefore behave exactly as they do
   in the REST API. It never references module implementations. ETags come
   from the stored hash and version (`IEntityTagEntry`), content types from
   the stored media type (`IMimeTypeDetector`), and an empty property store
   keeps no dead properties.
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
5. **Read-only first.** Slice 1 runs the library without class 2
   (`EnableClass2 = false`), so it advertises `DAV: 1` and Office opens files
   read-only. It serves `OPTIONS`, `PROPFIND` (depth 0/1; infinity is
   refused with `propfind-finite-depth`), `GET` and `HEAD`, with ranges and
   ETags. Writes answer 403.
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
     as tenant-owned rows (a `LockManagerBase` implementation). Win32
     properties are accepted but not stored.
   - Temporary files of Office and Explorer (`~$*`, `*.tmp`, `desktop.ini`,
     `Thumbs.db`, `._*`, `.DS_Store`) never become items.
   - Details are in the plan. (Built in WD-3; its "as built" notes list the
     differences, e.g. locks on paths and Office's safe save.)
8. **HTTPS is required.** Documentation shows the reverse-proxy setups we
   already have. It also describes the `BasicAuthLevel` registry change for
   tests on a LAN only (with a warning), and the `FileSizeLimitInBytes`
   change for larger downloads.

## Consequences

- We own a sizeable protocol library (about 25,000 lines once trimmed), with
  no active upstream. In exchange, it already does what Windows clients need
  (identity-keeping moves, correct status codes, `If` headers, locks, ETags,
  ranges), and its own unit tests come with it.
- The Minimal API endpoint keeps MVC out of the host. XML request bodies are
  read with `XmlSerializer` like upstream; trimming or Native AOT for the
  WebDAV endpoint is not a goal yet.
- WebDAV adds two tables in the `dav` schema (locks, temporary files) and two
  Documents contracts: bulk file metadata and uploads (`IDocumentUploads`).
- Allowing any file type changes behaviour for every client. Libraries that
  want only scans can use a workflow or a later per-library setting.
- Explorer cannot page, so very large folders are listed up to
  `WebDav:MaxFolderEntries` (default 5,000). Entries beyond that are left out
  and a warning is logged.
- `WebDav:Enabled` (default `true`) turns the endpoint off for an
  installation.
