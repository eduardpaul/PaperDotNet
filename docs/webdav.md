# WebDAV: libraries as a network drive

PaperDotNet serves document libraries over WebDAV (API-10), so Windows
Explorer, macOS Finder, rclone, Cyberduck and other WebDAV clients can browse
them and open their files. Design: [ADR-0047](adr/0047-webdav-for-libraries.md),
plan and slices: [webdav-plan.md](webdav-plan.md).

> **Status:** read-only (WD-1). Saving, creating, renaming and deleting files
> through WebDAV come with WD-3; until then, these operations are refused with
> `403 Forbidden`.

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

The web UI shows a library's address: open the library and choose
**Open in Explorer** (the drive icon). The API has it too:
`GET /v1.0/workspaces/{workspaceId}/lists/{listId}/webDav` → `{ "url": … }`.

## Signing in

WebDAV clients sign in with **HTTP Basic**:

- **User name:** anything (for example your user name). It is not checked.
- **Password:** an **API token** (Settings → API tokens) with the scopes
  `list.read` and `document.read`. **Open in Explorer → Create a token**
  opens the token dialog with these scopes selected.

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

Behind a reverse proxy:
- Forward `/dav` with the `Authorization` header unchanged. Do not put the
  proxy's own Basic authentication (such as an NPM access list) in front of
  `/dav`: it would replace the token.
- Allow the WebDAV methods (`OPTIONS`, `PROPFIND`, and with WD-3 `PUT`,
  `MKCOL`, `MOVE`, `COPY`, `DELETE`, `LOCK`, `UNLOCK`, `PROPPATCH`).
- Explorer first asks `OPTIONS /` at the server root. PaperDotNet answers it,
  so the proxy must forward the root as well.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Explorer keeps asking for the password | Not HTTPS (see above), a wrong or revoked token, or a token without `list.read` and `document.read` |
| "The folder you entered does not appear to be valid" | The WebClient service is not running, or the address is not under `/dav/` |
| `401` for every request with curl | Basic is only accepted under `/dav`; the password must be an API token |
| `403` on save, rename or delete | Read-only for now (WD-3) |
| A folder shows fewer items than the web UI | It has more than `WebDav:MaxFolderEntries` entries (warning in the log) |
| Download over 50 MB fails | `FileSizeLimitInBytes` (see above) |
