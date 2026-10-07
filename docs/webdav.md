# WebDAV: libraries as a network drive

PaperDotNet serves document libraries over WebDAV (API-10), so Windows
Explorer, macOS Finder, rclone, Cyberduck and other WebDAV clients can browse
them, and desktop apps such as Word or Excel can open and save their files.
Design: [ADR-0047](adr/0047-webdav-for-libraries.md), plan and slices:
[webdav-plan.md](webdav-plan.md).

## What you see

```
https://{your installation}/dav/
├── Home/                     your personal workspace
│   └── Documents/            a library
└── Projects/                 a workspace shared with you
    ├── Contracts/            a library
    │   ├── 2026/             a folder
    │   │   └── Lease.pdf     a document: its title + the file's extension
    │   └── Offer (2).pdf
    └── Invoices/
```

- **Levels:** workspaces, then their document libraries, then folders and
  documents. Other lists (tasks, events, plain lists) are not shown.
- **Permissions:** you see what you can read in the web UI. Folders and
  documents with unique permissions that you cannot read are not shown.
- **File names:** the document's title with the extension of its current file
  (`Lease` + `lease.pdf` → `Lease.pdf`).
  - Characters Windows does not allow (`\ / : * ? " < > |`) become `_`.
  - Reserved names such as `CON` or `NUL` get a `_` in front.
  - Names are cut at 255 characters.
  - When two entries of a folder get the same name, the newer ones get
    ` (2)`, ` (3)`, … (in the order they were created, so names stay stable).
- **Files:** the current version of each document, of any type (libraries
  take any file, see [documents.md](documents.md#file-types)). Downloads
  support ranges (`206`) and ETags, so clients can resume and cache. A browser
  that opens one never sniffs it or runs its scripts (`nosniff`, CSP
  `sandbox`).
- **Large folders:** a folder lists at most 5,000 entries
  (`WebDav:MaxFolderEntries`), because WebDAV clients cannot page. Use
  subfolders for more.

## What changing files does

Everything goes through the same rules as the web UI and the API: your
permissions, the size limit, the library's duplicate policy, versions, search,
the audit log and the library's workflows.

| In the client | In PaperDotNet |
|---|---|
| Save a file | A new **version** of the document (`document.added`, the library's workflows run) |
| Copy a file in | A new document, titled by the file name without its extension |
| New folder | A folder of the library |
| Rename | The title changes (and the file name; a PDF or image keeps its extension, since its type comes from its content) |
| Move to another folder | The same document in the other folder |
| Move to another library | The same document, with its versions and links (`MoveToAsync`) |
| Copy a file within the drive | A new document with the same content |
| Delete a file or folder | To the library's **recycle bin**, with everything in the folder |

- **Files created empty, then written** (Explorer does this when copying a
  file in) become one version, not two: the content that follows within
  `WebDav:EmptyFileGrace` (2 minutes) fills the empty version.
- **Office's safe save** works on mapped drives: Word and Excel write the new
  content into a temporary file, rename the original, rename the temporary
  file to the original name and delete the renamed original. PaperDotNet sees
  through this: the document keeps its identity, history, fields and links,
  and gets one new version.
- **Temporary files** of desktop apps (`~$Report.docx`, `~WRD0001.tmp`,
  Excel's `9A3B2C00`, LibreOffice's `.~lock.…#`, `Thumbs.db`, `desktop.ini`,
  `.DS_Store`, `._…`) are never documents. They are kept for the user who
  wrote them only (others do not see them), at most
  `WebDav:MaxTransientFileSize` (100 MB) each, and removed after a day.
- **Locks:** desktop apps lock a file while it is open (WebDAV class 2).
  Others can still open it, but saving it, moving it or deleting it fails
  with `423 Locked` until the app closes it. Locks last at most
  `WebDav:MaxLockTimeout` (1 hour; apps refresh them) and are kept in the
  database, so they hold across restarts and servers. The REST API and the
  web UI are not blocked by a WebDAV lock; their saves still check versions
  (`If-Match`).
- **Not supported:** writing part of a file (`Content-Range`, `501`), and
  client-defined properties (`PROPPATCH` of custom properties is `403`;
  Windows' file times and attributes are accepted but not stored).

The web UI shows a library's address: open the library and choose
**Open in Explorer** (the drive icon). The API has it too:
`GET /v1.0/workspaces/{workspaceId}/lists/{listId}/webDav` → `{ "url": … }`.

## Signing in

WebDAV clients sign in with **HTTP Basic**:

- **User name:** anything (for example your user name). It is not checked.
- **Password:** an **API token** (Settings → API tokens) with the scopes
  `list.read`, `document.read`, `list.write` and `document.write`.
  **Open in Explorer → Create a token** opens the token dialog with these
  scopes selected. A token with only `list.read` and `document.read` gives a
  read-only drive: files show as read-only, and apps open them read-only.

Your account password does not work here, and Basic is only accepted under
`/dav`. The token decides the tenant, so no tenant header or host name is
needed. Revoke the token to disconnect the drive everywhere.

## Windows Explorer

Windows sends Basic credentials only over **HTTPS**. Run PaperDotNet behind a
reverse proxy with a certificate (for example the
[Nginx Proxy Manager sample](../samples/nginx-proxy-manager/README.md)).

**Map network drive:**
1. In Explorer, choose **This PC → Map network drive**.
2. Paste the library's address, e.g.
   `https://dms.example.com/dav/Projects/Contracts/`, or `…/dav/` for all
   workspaces.
3. Select **Connect using different credentials**, then sign in with any user
   name and the API token as the password.

**Command prompt:**

```bat
net use * "https://dms.example.com/dav/Projects/Contracts/" /user:paperdotnet *
```

`*` as the drive letter takes the next free letter; the second `*` asks for
the password (paste the token). Add `/persistent:yes` to reconnect after a
restart.

**Things to know about Explorer:**
- **WebClient service.** Explorer's WebDAV support is the *WebClient* service.
  It runs on Windows 10 and 11. On Windows Server, install the
  *WebDAV Redirector* feature first.
- **Changes appear late.** Explorer caches folder listings for a while. Press
  **F5** to refresh.
- **Files over 50 MB** fail to download with error `0x800700DF`. Raise the
  limit in the registry (up to 4 GB), then restart the WebClient service:

  ```bat
  reg add HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters /v FileSizeLimitInBytes /t REG_DWORD /d 4294967295 /f
  net stop webclient && net start webclient
  ```

- **Testing on a local network without HTTPS.** Windows can be told to send
  Basic credentials over plain HTTP. **Only do this for tests on a network you
  trust:** the token travels unencrypted, and the setting applies to every
  WebDAV server the PC talks to.

  ```bat
  reg add HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters /v BasicAuthLevel /t REG_DWORD /d 2 /f
  net stop webclient && net start webclient
  ```

  Set it back to `1` (Basic over HTTPS only, the default) when you are done.

## Other clients

**rclone:**

```bash
rclone config create paperdotnet webdav \
  url=https://dms.example.com/dav/ vendor=other \
  user=paperdotnet pass="$(rclone obscure 'pdn_…')"
rclone ls paperdotnet:Projects/Contracts
rclone mount paperdotnet: ~/PaperDotNet --read-only
```

**Cyberduck / Mountain Duck:** a new *WebDAV (HTTPS)* bookmark with the
server, the path `/dav/`, any user name and the token as the password.

**macOS Finder:** **Go → Connect to Server**, then
`https://dms.example.com/dav/`, any user name and the token.

**curl:**

```bash
curl -u paperdotnet:pdn_… -X PROPFIND -H 'Depth: 1' https://dms.example.com/dav/Projects/Contracts/
curl -u paperdotnet:pdn_… -O https://dms.example.com/dav/Projects/Contracts/Lease.pdf
```

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `WebDav:Enabled` (`PAPERDOTNET__WebDav__Enabled`) | `true` | Serve `/dav`. When `false`, `/dav` answers `404` and the address endpoint `404 webDavDisabled`. |
| `WebDav:MaxFolderEntries` | `5000` | Most entries one folder lists; larger folders are cut off with a warning in the log. |
| `WebDav:EmptyFileGrace` | `00:02:00` | A file created empty and written within this time gets one version, not two. |
| `WebDav:MaxTransientFileSize` | `104857600` | Largest temporary file of a desktop app (Office writes the whole document into one). |
| `WebDav:MaxLockTimeout` | `01:00:00` | Longest lock; longer or infinite requests are shortened. |

Documents written through WebDAV follow `Documents:MaxFileSize` (100 MB) like
any upload. Expired locks and temporary files are removed by the recurring job
`dav.cleanup` (hourly).

Behind a reverse proxy:
- Forward `/dav` with the `Authorization` header unchanged. Do not put the
  proxy's own Basic authentication (such as an NPM access list) in front of
  `/dav`: it would replace the token.
- Allow the WebDAV methods (`OPTIONS`, `PROPFIND`, `PUT`, `MKCOL`, `MOVE`,
  `COPY`, `DELETE`, `LOCK`, `UNLOCK`, `PROPPATCH`) and request bodies as large
  as `Documents:MaxFileSize` (Nginx: `client_max_body_size`; the Nginx Proxy
  Manager sample allows 520 MB).
- Forward the `Destination` header unchanged (MOVE and COPY), with the
  public scheme and host.
- Explorer first asks `OPTIONS /` at the server root. PaperDotNet answers it,
  so the proxy must forward the root as well.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Explorer keeps asking for the password | Not HTTPS (see above), a wrong or revoked token, or a token without `list.read` and `document.read` |
| "The folder you entered does not appear to be valid" | The WebClient service is not running, or the address is not under `/dav/` |
| `401` for every request with curl | Basic is only accepted under `/dav`; the password must be an API token |
| `403` on save, rename or delete | The token lacks `list.write` and `document.write`, you may only read the library or folder, or a PDF or image was renamed to another extension |
| `409` when copying a file in | The library blocks duplicates and the file exists already (the web UI shows where) |
| `423 Locked`, or "The file is in use" | Someone has it open in a desktop app; it unlocks when they close it, or after an hour |
| Word or Excel opens files read-only | The token has no write scopes, or you may only read the library |
| A folder shows fewer items than the web UI | It has more than `WebDav:MaxFolderEntries` entries (warning in the log) |
| Download over 50 MB fails | `FileSizeLimitInBytes` (see above) |
