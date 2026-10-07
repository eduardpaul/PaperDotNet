# Plan: WebDAV for libraries (API-10)

Status: **in progress** (WD-0 to WD-3 done; guide: [webdav.md](webdav.md)). Decisions: [ADR-0047](adr/0047-webdav-for-libraries.md).
Idea: [0018](../ideas/0018-webdav-access-to-libraries.md).

## Goal

A member maps PaperDotNet as a network drive in **Windows Explorer**:

```
Map network drive → https://docs.example.com/dav/
User name: anything   Password: an API token (pdn_…)
```

They then browse their workspaces, document libraries and folders, and open
files in desktop apps. The first slice is **read-only**. Writes come in a
later slice, where saves create file versions and new files go through the
library's normal workflows (text, thumbnails, OCR).

**Decided with the product owner (2026-10-06):**

| Topic | Decision |
|---|---|
| Protocol library | FubarDev.WebDavServer `release/2.0` (MIT), **vendored** into `src/BuildingBlocks/PaperDotNet.WebDav` (decided after the [spikes](../spikes/webdav-poc-fubar/README.md)) |
| Authentication | HTTP Basic with an existing API token as the password |
| First slice | Read-only (browse, download), writes later |
| File types | Libraries accept **any** file type (DOC-02 change), before writes |
| Layout | `/dav/{workspace}/{library}/{folder…}/{file}` |
| Names | Title + extension, Windows-safe, duplicates get ` (2)`, ` (3)` in id order |
| TLS | HTTPS required; the registry change for plain HTTP is documented for tests on a LAN only |

**Not in scope:** CalDAV/CardDAV (CAL-05/06, same `Dav` module later); lists
that are not libraries; metadata in sidecar files; macOS/Linux-specific
fixes beyond what comes for free (WD-4); NTLM/Kerberos.

## Target shape

```
Windows Explorer / Office / rclone
        │ HTTPS, Authorization: Basic <any>:<pdn_ token>
        ▼
/dav  ── ApiToken handler (Basic accepted only under /dav) → tenant claim → TenantGuard
        │
        ▼
PaperDotNet.WebDav  (vendored FubarDev.WebDavServer: dispatcher, handlers, If/ETag, ranges, locks; MapWebDav endpoint)
        │ IFileSystemFactory → IFileSystem / ICollection / IDocument, IMimeTypeDetector,
        │ IPropertyStoreFactory (empty store), LockManagerBase (WD-3)
        ▼
Dav module (src/Modules/Dav)  ── SDK contracts only
        ├─ IWorkspaceAccess        workspaces the caller can read
        ├─ IListItemStore          libraries, folders, items, moves, recycle bin
        └─ Documents.Contracts     file metadata, streams; uploads/versions (WD-3)
```

**Module rules (architecture tests):**
- `PaperDotNet.WebDav` references only the ASP.NET Core framework.
- Only `PaperDotNet.Dav` references `PaperDotNet.WebDav`.
- `PaperDotNet.Dav` references contracts only (Lists, Documents, Workspaces,
  Abstractions), never module implementations or provider packages.

## Resource model

| Path | Resource | Source |
|---|---|---|
| `/dav/` | Collection: workspaces the caller can read | `IWorkspaceAccess.GetMyWorkspacesAsync` + `GetNamesAsync`; Home via `EnsurePersonalWorkspaceAsync` (shown as `Home`) |
| `/dav/{ws}/` | Collection: the workspace's document libraries | `IListItemStore.GetListsAsync(ws)` where `IsLibrary` |
| `/dav/{ws}/{lib}/…/` | Collection: a folder | `ListChildrenAsync(…, folderId)` where `IsFolder` |
| `/dav/{ws}/{lib}/…/{name}.{ext}` | File: the item's current file | `ListChildrenAsync` items + bulk file metadata |

**Properties (PROPFIND):**

| Property | Value |
|---|---|
| `displayname` | the Windows-safe name |
| `resourcetype` | `collection` for workspaces, libraries and folders |
| `getcontentlength` | size of the current file version |
| `getcontenttype` | media type of the current file version |
| `getetag` | `"{sha256[..16]}-{item.Version}"` (changes when the content or the item changes) |
| `getlastmodified` | the item's `UpdatedAt` (RFC 1123) |
| `creationdate` | the item's `CreatedAt` (ISO 8601) |
| `Win32FileAttributes` (MS) | `00000001` (read-only) while the caller cannot write (always in WD-1) |
| `quota-available-bytes` / `quota-used-bytes` | WD-4 (so Explorer shows a drive size) |

**Name rules** (`DavNames`, unit-tested):
1. The file name is the title plus the extension of the current file name. If
   that name has no extension, the extension comes from the media type
   (`.pdf`, `.tiff`, `.jpg`, `.png`, `.webp`).
2. These characters become `_`: `\ / : * ? " < > |` and control characters.
   Leading and trailing spaces are trimmed, and so are trailing dots. An empty
   name becomes `_`.
3. Reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`,
   `LPT1`–`LPT9`) get a `_` suffix, also when they have an extension
   (`CON.pdf` → `CON_.pdf`).
4. Names are cut to 255 UTF-16 units, keeping the extension.
5. Siblings are grouped by name, case-insensitively (ordinal ignore case).
   Within a group, the item with the lowest id keeps the name and the others
   get ` (2)`, ` (3)`, … before the extension. When a suffixed name collides
   with a real title, the suffixing repeats until every name is unique.
6. Workspaces and libraries follow the same rules among their siblings.

**Path resolution:** each segment is resolved by listing its parent once and
matching the computed names (exact match first, then case-insensitive). The
listing is cached for the request (scoped `DavResolver`), so a depth-1
PROPFIND reads every level once. A short tenant- and user-keyed `HybridCache`
for path → id is an optimisation for WD-4, if measurements show that we need
it (it would carry `AccessCacheTags.Principals`).

## Slices

### WD-0: Vendor the library (building block) ✅

1. Copy `src/FubarDev.WebDavServer` and `src/FubarDev.WebDavServer.Models`
   of `release/2.0` at `1f78db5` into one project,
   `src/BuildingBlocks/PaperDotNet.WebDav`.
   - Keep the upstream namespaces (`FubarDev.WebDavServer.*`), so upstream
     diffs stay readable.
   - Keep `LICENSE` (MIT, © 2016 Fubar Development Junker) next to the code,
     and add a `README.md` with the upstream URL, the commit and a change log
     of our edits.
   - Mark the vendored files as generated code in `.editorconfig`, so the
     solution's analyzers and `dotnet format` leave the upstream style alone.
2. Retarget to net10.0 only and fix the 16 compile errors the spike found
   (`Lock` vs `System.Threading.Lock`, `System.Linq.Async` vs .NET 10
   `AsyncEnumerable`, one removed `Uri` constructor).
3. Remove dependencies:
   - Digest authentication and the user-agent parser (unused);
   - Scrutor (explicit handler registrations);
   - System.Interactive.Async (in .NET 10);
   - Yoakke (six nightly packages): replace the generated parser with a
     small parser for the `If`, entity-tag and `Lock-Token` headers.
4. Replace the MVC layer with a Minimal API endpoint: `MapWebDav(pattern)`
   maps every WebDAV method to the dispatcher, reads XML bodies with
   `XmlSerializer`, and turns `WebDavException`s into responses. The request
   context is the upstream `EscapedWebDavContext`, reading the route value
   `path`.
5. Fixes from the spike:
   - folder MOVE through an optional `IMovableCollection` (identity kept);
   - `Timeout: Infinite, Second-4100000000`;
   - `Content-Length` on HEAD;
   - PROPPATCH without a body → 400;
   - malformed `If-None-Match`/`If-Match` → ignored, not 500.
6. Port the upstream unit tests of the library itself (headers, entity
   tags, ranges, URI comparison, converters) to
   `tests/PaperDotNet.UnitTests/WebDav` (xunit v3). Protocol behaviour is
   covered by the Dav integration tests and litmus.
7. Register it in `docs/dependency-licenses.md` (vendored, MIT) and in
   `THIRD-PARTY-NOTICES.md` (MIT requires the notice in copies).

**Done when:** the solution builds, the ported tests pass, and the
architecture test "only Dav references WebDav" exists.

### WD-1: Read-only mount (first delivery) ✅

**Authentication (Identity module)**
- `ApiTokenAuthenticationHandler` reads `Authorization: Basic base64(user:pdn_…)`
  when the request path starts with `/dav` (`WebDav:Path`). The password is the
  token and the user name is ignored. Everything else is unchanged.
- The default policy scheme forwards a request under `/dav` with Basic to the
  API token scheme. Elsewhere, Basic is never read (test: `Basic` on `/v1.0/me` → 401).
- The challenge under `/dav` is `401` + `WWW-Authenticate: Basic realm="PaperDotNet", charset="UTF-8"`.
- The tenant comes from the token claim (existing claim strategy on the
  `ApiToken` scheme). `TenantGuardMiddleware` behaves as for Bearer tokens.
- Failed attempts are logged and audited like Bearer token failures. Rate
  limiting uses the existing per-tenant + client limiter: Explorer sends many
  requests, so `/dav` gets its own partition with a higher limit (measure in
  WD-1).

**Documents contracts**
- `IDocumentFileStore.GetCurrentAsync(IReadOnlyCollection<Guid> itemIds, ct)`
  → `IReadOnlyDictionary<Guid, DocumentFile>` (one query for a folder listing,
  with access checked).
- `DocumentFile` gets `CreatedAt` (the version's time) if `getlastmodified`
  should follow the file rather than the item. To decide in review; the
  default uses the item's `UpdatedAt`.

**Dav module (`src/Modules/Dav/PaperDotNet.Dav`)**
- `DavModule : IModule`. It has no DbContext yet, and maps `/dav` when
  `WebDav:Enabled` (default `true`).
- Options `WebDavOptions`:
  - `Enabled`;
  - `Path` (`/dav`);
  - `MaxFolderEntries` (5,000);
  - `MaxLockTimeout` (WD-3).
- `DavFileSystemFactory : IFileSystemFactory` creates one `DavFileSystem` per
  request and user, with `DavRoot`, `DavWorkspace`, `DavFolder` (libraries and
  folders, `ICollection`) and `DavDocument` (`IDocument`, `IEntityTagEntry`):
  - `OpenReadAsync` → `IDocumentFileStore.OpenVersionAsync(current.Id)`;
  - write members throw `WebDavException(Forbidden)` in WD-1.
- `DavMimeTypeDetector : IMimeTypeDetector` answers the stored media type.
- `EmptyPropertyStore`: dead properties are not stored. In WD-3, `PROPPATCH`
  of Win32 properties answers 200 without storing them (live properties).
- `OPTIONS` (on `/dav` and below, anonymous) is answered by the library:
  `DAV: 1` without class 2, `MS-Author-Via: DAV`.
- **Spike item:** Explorer sometimes sends `OPTIONS /` and `PROPFIND /` to
  the server root before the mapped path. If it does, the host answers
  `OPTIONS /` without an `Origin` header (so not a CORS preflight) with the
  same DAV headers, and `PROPFIND /` with `405`. This lives in the Dav module,
  registered before the web UI fallback.
- `PROPFIND`:
  - `Depth: infinity` → 403 `propfind-finite-depth`;
  - at most `MaxFolderEntries` children, with a warning in the log when a
    folder has more;
  - folder listing goes through `ListChildrenAsync` pages of 1,000;
  - files come from one bulk metadata call per page.
- `GET` / `HEAD`:
  - `Content-Type`, `Content-Length`, `ETag`, `Last-Modified`;
  - `Range` and `If-None-Match` / `If-Modified-Since` (304).

**Discovery in the API and web UI**
- `GET /v1.0/workspaces/{ws}/lists/{list}/webDav` → `{ "url": "https://…/dav/Projects/Contracts/" }`
  (scope `list.read`, 404 below Read, not a library → 404). The server builds
  the path with `DavNames`, so the UI never duplicates the name rules. When
  WebDAV is off it returns `404 webDavDisabled`.
- Add it to the OpenAPI snapshot and regenerate the SDKs.
- Web UI: on a library, the action **"Open in Explorer…"** opens a dialog
  with:
  - the WebDAV address and a copy button;
  - Windows steps (Map network drive, `net use`);
  - a link to **Settings → API tokens** with the scopes `list.read` and
    `document.read` preselected (`/settings/tokens?scopes=…`; add the search
    params if missing).
  - It gets a Playwright test.
- Personal API tokens UI: a hint that a token can be used as a WebDAV password.

**Docs**
- New guide `docs/webdav.md`:
  - mapping in Explorer, `net use Z: https://host/dav/ /user:me <token>`,
    rclone and Cyberduck;
  - HTTPS through the existing reverse-proxy samples;
  - the LAN-only `BasicAuthLevel=2` registry change, with a warning;
  - `FileSizeLimitInBytes` for files over 50 MB;
  - the WebClient service on Windows Server;
  - Explorer's caching (F5) and limits.
- Nginx Proxy Manager sample: `/dav` must not be behind the proxy's own Basic
  access list (it would replace our Basic header). Add a location without an
  access list, `client_max_body_size` (WD-3) and `proxy_request_buffering off`.
- Update `features.md` (API-10 row), `technical-approach.md` §11 and
  `frontend.md` (library actions).

**Tests (integration, SQLite and PostgreSQL)**
- PROPFIND depth 0/1 at every level returns the expected names, properties and
  hrefs (percent-encoded, trailing slash on collections).
- Name rules: sanitizing, reserved names, duplicates and stable suffixes.
- GET: content, ranges (`206`), conditional requests (`304`), `HEAD`.
- Auth:
  - no credentials → 401 with the Basic challenge;
  - a wrong, revoked or expired token → 401;
  - a token without `document.read` → 403;
  - Basic outside `/dav` is ignored.
- **Tenant isolation:** a token of tenant A sees nothing of tenant B, and an
  explicit other tenant (host mapping) → 403 `tenantMismatch`.
- **Permissions:** a folder with unique permissions that the caller cannot
  read is invisible, and its path → 404. Workspaces without access are not
  listed. Lists that are not libraries are not listed.
- Writes (`PUT`, `DELETE`, `MKCOL`, `MOVE`, `COPY`, `LOCK`, `PROPPATCH`) → 403
  in WD-1.
- `WebDav:Enabled=false` → `/dav` is 404 and the discovery endpoint is
  `404 webDavDisabled`.
- Large folder: 5,001 items → 5,000 entries and a logged warning.
- Litmus (`litmus` test suite, run in a container in an optional CI job;
  GPL-2.0, test-only and not shipped): the `basic` and `props` read tests
  pass.

**Manual Windows checklist (per release until automated)**
- Windows 11:
  - map `https://host/dav/` with a token;
  - browse three levels;
  - open a PDF and a 60 MB file (with the registry change);
  - names with umlauts, `#` and `%`;
  - a duplicate title;
  - a revoked token prompts again.
- Office opens a `.docx` read-only.
- `net use` from cmd.

**Done when:** a Windows 11 machine maps the drive over HTTPS, browses and
opens files, every test above passes on both providers, and the docs are
merged.

### WD-2: Any file type in libraries (DOC-02 change) ✅

- `FileTypes.Detect` stays for the types we process.
  - An unknown header is no longer `415 unsupportedFileType`: the file is
    accepted.
  - Its media type comes from the upload's file name, through
    `FileExtensionContentTypeProvider`, or is `application/octet-stream`.
  - Files are still never trusted by name for *processing*.
- Empty files (0 bytes) are accepted, because Explorer creates files that way.
- Built-in workflows (`documents.text`, `.thumbnail`, `.pages`, `.ocr`) check
  the media type in their condition and skip other files. The run shows
  "skipped", not failed. Storage optimisation (ADR-0046) skips them too.
- Search: non-PDF/image files have no text yet. Text extraction for Office
  files is a separate later feature (an idea to add).
- The web UI shows a generic file icon when there is no thumbnail.
- Downloads of unknown types are sent with `Content-Disposition: attachment`
  and `X-Content-Type-Options: nosniff` (no inline HTML or SVG from user
  content).
- Tests: uploading `.docx`, `.txt`, `.html` and an empty file through REST,
  MCP and packages; workflows skip them; duplicate policy and size limit still
  apply.
- Docs: `documents.md` (what happens to an uploaded file), `features.md`
  (DOC-02 note).

### WD-3: Writes and locking ✅

**As built** (differences from the plan below):
- `IDocumentUploads` (Documents.Contracts) has `UploadAsync`, `ReplaceAsync`
  (with `expectedSha256` and `replaceEmptyWithin`) and `RenameAsync` (the
  current file's name; a PDF or image keeps its extension). REST and MCP
  uploads share the same `DocumentService` code.
- The library got two small additions: `IContentCollection`/`IContentDocument`
  (a PUT is one upload) and `IMovableEntry` (MOVE keeps identity; the file
  system decides what replacing a target means), plus failures keeping the
  file system's status in COPY results.
- Locks are on paths, as the library expects them, not bound to item ids.
  `Home/…` locks only apply to their holder (every user has their own Home).
  The library's per-request implicit locks are stored with an expiry of
  `MaxLockTimeout`, so a broken request cannot block a file.
- `COPY` is in (a new document per file), not WD-4.
- `DELETE` of a folder recycles its content first (no 409).
- A PUT below a missing folder answers `404` (upstream), not `409`.
- Temporary files may be as large as documents (`MaxTransientFileSize`,
  100 MB): Office writes the whole document into `~WRD….tmp`. Excel's
  8-hex-digit temporary names count too.
- Office's safe save: renaming a document to a temporary name in the same
  folder hides it under that name (`transient_files.item_id`); moving a
  temporary file onto the original name stores a new version of that document
  and shows the renamed entry with the old content until the app deletes it.
- litmus: basic 16/16, copymove 13/13, locks 35/41 (the three conditional-PUT
  cases upstream fails too, and `owner_modify` because custom properties are
  not stored), props 11/14 (custom properties, by design).

**Documents contracts:**
```csharp
public interface IDocumentUploads
{
    Task<DocumentWriteResult> UploadAsync(Guid workspaceId, Guid listId, Guid? folderId, Stream content,
        string fileName, string? title, CancellationToken cancellationToken);
    Task<DocumentWriteResult> ReplaceAsync(Guid itemId, Stream content, string fileName,
        string? expectedETag, CancellationToken cancellationToken);
}
// Status: Ok, NotFound, Forbidden, TooLarge, DuplicateBlocked, VersionMismatch, Rejected
```
The REST endpoints and MCP tools move onto the same implementation, so the
behaviour cannot drift.

**Method mapping:**

| Method | Behaviour |
|---|---|
| `PUT` new name | `UploadAsync` into the folder. The title is the name without its extension. Duplicate `Block` → 409. Too large → 413. |
| `PUT` existing file | `ReplaceAsync`. A new file version raises `document.added`. `If-Match` is checked against `getetag`; a mismatch → 412. |
| `PUT` 0 bytes then content (Explorer) | The first `PUT` creates the item with an empty file. The next `PUT` within `WebDav:EmptyFileGrace` (default 2 min) by the same user replaces the empty version instead of adding a version. Workflows skip empty files, so the item is processed once. |
| `MKCOL` | `CreateFolderAsync`. A name that exists → 405. Libraries without folders → 403. |
| `MOVE` same parent, new name | Title update (`UpdateAsync` with the expected version). If the extension changes, the stored file name changes too (new file name on the next version, or a metadata update: to decide). |
| `MOVE` other folder, same list | `MoveAsync` |
| `MOVE` other library | `MoveToAsync` (keeps identity and versions). Incompatible content types → 409. |
| `MOVE` with `Overwrite: T` onto an existing file | The target goes to the recycle bin, then the move happens. If the target is locked by someone else → 423. |
| `DELETE` | `DeleteAsync` → recycle bin. Folders: the store rejects non-empty folders. Explorer deletes children first, so for others we answer 409 and document it. |
| `COPY` | WD-4 |
| `PROPPATCH` | Win32 times and attributes: 207 with 200 for each property, not stored. Other properties → 403 per property. |
| `LOCK` / `UNLOCK` | Exclusive write locks only, with depth 0 for files and infinity for folders. Timeout up to `MaxLockTimeout` (default 1 h). `DAV: 1, 2` is advertised from WD-3. |

**Locks:**
- `DavDbContext` in schema `dav` with table `locks`: `TenantId`, `Id` (token),
  `Path`, `ItemId`, `OwnerUserId`, `Owner` (XML), `Depth`, `ExpiresAt`.
  It implements `ITenantOwned`, and RLS on PostgreSQL is generated.
- Migrations for both providers.
- `EfLockManager : LockManagerBase` (the library's lock logic, our storage). Expired locks are cleaned up by a recurring
  job (`ITenantRecurringJob`, hourly).
- Locks bind to the `ItemId` once resolved, so a rename does not lose them.
- The REST API is not blocked by a WebDAV lock. The item shows "locked by X
  in a desktop app" (live event `document.locked`): to decide.
  `If-Match` still protects against lost updates.

**Temporary files:**
- Names matching `~$*`, `*.tmp`, `~*.tmp`, `desktop.ini`, `Thumbs.db`,
  `.DS_Store`, `._*`, `*.~lock.*#` are held in a per-user transient store and
  never become items:
  - `dav.transient_files`: rows in the database, content in the blob store
    under a `dav/` prefix;
  - at most 10 MB each;
  - they expire after 24 h.
- They appear in PROPFIND only to the user who created them.
- Office's save pattern (write a temp file, then MOVE the temp file onto the
  original) becomes `ReplaceAsync` on the original, so the original's identity
  and history are kept.

**Events and loops:** writes go through the store and Documents pipelines, so
mutators, versions, `document.added`, search and audit work unchanged.
Nothing new reacts to events, so no `EventCausation` change is needed.

**Tests:**
- every row of the method table;
- Office save sequence replay, recorded from a real Windows client in WD-1;
- Explorer copy-in of a tree with `MKCOL` + `PUT`;
- rename and move across libraries;
- lock conflicts (423), lock expiry and the recurring cleanup;
- tenant isolation for locks and transient files;
- RLS on PostgreSQL;
- litmus `basic`, `copymove` (without COPY), `locks` and `props`.

**Manual:**
- edit and save a `.docx` and an `.xlsx` in Office (the version count grows
  by one per save);
- drag a folder tree in;
- rename;
- delete, then restore from the recycle bin in the web UI.

### WD-4: Polish

- `COPY` (within and across libraries, as new items with the current file).
- Quota properties (from tenant quotas when PLT-06 lands; otherwise free disk
  space is not reported).
- Path cache in `HybridCache`.
- Performance test: PROPFIND of 5,000 entries under 1 s on SQLite.
- rclone, Cyberduck and macOS Finder (`._` files, `.DS_Store`) and the GNOME
  `davs://` checklist.
- Optional read-only views of version history (`/dav/…/file.pdf@v3`): only if
  users ask.

## Risks

| Risk | Mitigation |
|---|---|
| Vendored library has protocol bugs Explorer hits | Its unit tests; our integration tests; litmus job; manual Windows checklist; we own the code and can fix it |
| Vendored library is large (about 25,000 lines) without an upstream | Upstream namespaces and a change log keep our edits visible; only the Dav module uses it |
| Explorer quirks (root `OPTIONS`, 50 MB limit, caching, Basic over HTTP) | Spike in WD-1 against a real Windows 11 machine; documented registry values; root `OPTIONS` handler |
| Large folders are slow (Explorer cannot page) | Bulk file metadata, a per-request cache, `MaxFolderEntries` cap, a performance test in WD-4 |
| Basic credentials are ambient (CSRF) | Basic is accepted only under `/dav`; WebDAV write methods need CORS preflight, and `/dav` sends no CORS headers; reads expose nothing to other origins |
| Tokens leak through Windows Credential Manager | Users create a dedicated token with a minimal scope and expiry; revoking it in the UI ends access at once |
| Any file type → risky content (HTML, SVG) | Always `attachment` + `nosniff` for types we do not render; no inline preview for them |
| Office temp file patterns change | Patterns are configurable (`WebDav:TransientPatterns`) |

## Open questions (for review of the ADR)

1. **`getlastmodified`:** from the item (`UpdatedAt`, so metadata edits
   change it) or from the current file version? Proposed: the item, because
   ETags already follow both.
2. **A `dav.use` scope**, like `mcp.use`, so administrators can turn WebDAV
   off per role? Proposed: not now (`WebDav:Enabled` per installation, scopes
   per token).
3. **Rename changes the extension** (`a.pdf` → `a.txt`): refuse (403), or
   accept and change the stored file name? Proposed: accept, and keep the
   detected media type.
4. **WebDAV locks visible in the web UI** (a "locked in a desktop app" badge,
   and blocking REST edits)? Proposed: a badge only, no blocking; decide in
   WD-3.
