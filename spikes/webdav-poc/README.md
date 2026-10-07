# Spike: WebDAV with Dav.AspNetCore.Server

> **Follow-up:** [../webdav-poc-fubar](../webdav-poc-fubar/README.md) runs the same checks against
> FubarDevelopment/WebDavServer (`release/2.0`) and compares the two. Its verdict: Fubar is the better
> base for the fork.

A throwaway proof of concept for [webdav-plan.md](../../docs/webdav-plan.md) /
[ADR-0047](../../docs/adr/0047-webdav-for-libraries.md). It checks what the
library can do **before** we vendor it or change any product code.

It is not part of `PaperDotNet.slnx`. Its own `Directory.Build.props` and
`Directory.Packages.props` keep it out of the repository's build rules.

**Where the results come from:**
- Linux, .NET SDK 10.0.401, litmus 0.13, 2026-10-06.
- **No real Windows client was used.** Explorer and Office are imitated by
  request sequences in `check.py`.

## What it is

| File | What it does |
|---|---|
| `PocModel.cs` | In-memory workspaces → libraries → folders → documents. Includes a list that is not a library, duplicate titles, characters Windows does not allow, `CON`, an item without a file, a read-only document, a 3 MB file, a folder named `Ä Umlaut & #hash 100% [x]` and a folder with 5,000 documents. |
| `DavNames.cs` | The plan's name rules: title + extension, Windows-safe, ` (2)` suffixes in creation order, Office temp-file patterns. |
| `PocStore.cs` | The adapter the Dav module would implement: `IStore`, `IStoreCollection`, `IStoreItem`. It also covers properties (ETag from hash + version, Win32 attributes, quota), PUT as a new item or a new version, MKCOL, MOVE that keeps identity, the recycle bin, and Office safe-save. |
| `Program.cs` | Basic auth with the token as the password, only under `/dav`, with a challenge. A gate that answers `OPTIONS` (`DAV: 1` read-only or `1, 2`, `MS-Author-Via`) and refuses writes in read-only mode. A root `OPTIONS /` probe. A `/v1.0/me` endpoint to prove Basic is ignored outside `/dav`. |
| `check.py` | 50 read-only and 76 read-write checks: Explorer-style PROPFIND, ranges, conditional requests, the PUT/MKCOL/MOVE/COPY/DELETE/PROPPATCH/LOCK sequences, folder moves that keep identity, Office safe-save. Also used by [../webdav-poc-fubar](../webdav-poc-fubar/README.md). |
| `run.sh` | Builds the PoC, starts it read-only and read-write, runs `check.py`, then runs every litmus suite. |
| `library-fixes.patch` | The fixes we needed in the library (against git HEAD `7ef7cbf`). |
| `results/` | The output of each run below. |

```bash
./run.sh                                     # NuGet package 1.0.1
git clone https://github.com/ThuCommix/Dav.AspNetCore.Server /tmp/dav
./run.sh /tmp/dav/src/Dav.AspNetCore.Server/Dav.AspNetCore.Server.csproj      # git HEAD
git -C /tmp/dav apply "$PWD/library-fixes.patch" && ./run.sh /tmp/dav/src/Dav.AspNetCore.Server/Dav.AspNetCore.Server.csproj  # patched
```

## Results

| Library | check.py read-only | check.py read-write | litmus basic | copymove | props | locks | http |
|---|---|---|---|---|---|---|---|
| NuGet 1.0.1 (May 2023) | 46/50 | 56/76 | 14/16 | 12/13 | 9/14 | 9/13 (stops early) | 4/4 |
| git HEAD `7ef7cbf` (Oct 2025) | 48/50 | 59/76 | 14/16 | 12/13 | 9/14 | 9/13 (stops early) | 4/4 |
| HEAD + `library-fixes.patch` | **50/50** | **76/76** | **16/16** | **13/13** | 9/14 ¹ | 38/41 ¹ | **4/4** |
| … + dead properties in memory (earlier 75-check run) | 50/50 | 75/75 | 16/16 | 13/13 | 15/30 ² | **41/41** | 4/4 |

¹ All remaining failures in `locks`, and 3 in `props`, come from dead properties
(PROPPATCH of client-defined properties). The plan does not store them (a
no-op store). The other 2 `props` failures: PROPFIND with malformed XML
answers 207 instead of 400.
² Storing dead properties is not complete: values are not returned, and a
remove and set in one request fails. We did not investigate whether the cause
is the library or the PoC's in-memory store, because the plan does not need
dead properties.

**Performance:** an Explorer-style PROPFIND of the 5,000-document folder
takes 0.42–0.63 s and returns 5.0 MB. The library builds the whole
multistatus in memory, which is fine at the 5,000 cap.

## Findings

### Works as the plan assumes

- **The store interfaces fit our model.** Workspaces, libraries and folders
  are collections, documents are items. Lookup goes by computed name, with
  one listing per collection per request.
- **Custom properties** are registered per item type, read from our data,
  and are not "expensive":
  - `getetag` comes from sha256 + item version, so the content is never read
    for it;
  - Win32 attributes report read-only (`00000001`) when the caller cannot
    write;
  - `quota-*` are reported;
  - Win32 PROPPATCH is accepted and ignored through a change callback.
  - Unknown properties get a 404 propstat.
- **Authentication:**
  - the library challenges through the ASP.NET Core default scheme;
  - `OPTIONS` stays anonymous;
  - a handler that reads Basic only under `/dav` works;
  - Basic on `/v1.0/me` is ignored.
- **`Depth: infinity` refused** (`DisallowInfinityDepth`), `HEAD`,
  `If-None-Match` / `If-Modified-Since` → 304.
- **The path base works.** Hrefs and `Destination` handle `app.Map("/dav")`.
- **Name rules:**
  - duplicates and sanitizing work;
  - a list that is not a library is hidden, and so is an item without a file;
  - case-insensitive paths work (see the store workarounds below).
- **Locking works** with the fixes: exclusive and shared locks, 423 without
  a token, refresh, unlock, the timeout cap, and LOCK on a new name.
  `InMemoryLockManager` was used here; the plan's EF lock manager implements
  the same `ILockManager`.
- **Explorer's 0-byte PUT followed by the content** gives one version, as the
  plan's "replace an empty version" rule intends.
- **A safe-save sequence like Office's** (PUT temp, MOVE original → `~RF….TMP`,
  MOVE temp → original, DELETE backup; the exact Office sequence is not verified) can keep the original item and add one version.
  This works with the store logic in `PocStore.MoveItemAsync`, but **only
  with the patched MOVE**.

### Bugs in the library (all fixed in `library-fixes.patch`, about 90 lines)

| # | Bug | Effect for us | In 1.0.1 | In HEAD |
|---|---|---|---|---|
| 1 | **MOVE is COPY + DELETE**; `IStoreCollection.MoveItemAsync` is never called | Rename or move creates a new item: id, versions, comments and relations are lost, and the old item goes to the recycle bin. Breaks every rename in Explorer. | yes | yes |
| 2 | Child names reach the store percent-encoded (`Invoice%20(2).pdf`) | Any name with a space, umlaut, `#` or `%` is 404 | yes | fixed |
| 3 | PUT ignores the status of `WriteDataAsync` | 413 (too large), 403 (no Contribute), 409 (duplicate blocked) all reach the client as **200**. Explorer would report success for a failed save. | yes | yes |
| 4 | PUT always answers 200 (not 201/204); COPY/MOVE answer 200 for new resources | RFC violations, litmus warnings | yes | yes |
| 5 | GET sends the ETag unquoted; `If-Range` compares unquoted | Invalid `ETag` header, caching and resume break | yes | yes |
| 6 | Range end is exclusive (`bytes=0-9` returns 9 bytes); `Content-Range` is wrong for suffix ranges | Corrupt partial downloads for any client that resumes or reads ranges | yes | yes |
| 7 | LOCK response has no `Lock-Token` header | RFC 4918 requires it; clients read it | yes | yes |
| 8 | LOCK rejects a text `<owner>` (400) | litmus fails; any client with a plain-text owner cannot lock | yes | yes |
| 9 | `Timeout: Infinite` ignores `MaxLockTimeout` (infinite locks) | Stale locks never expire | yes | yes |
| 10 | LOCK on an unmapped URL does not create the resource (200 instead of 201) | Clients that lock a new name before writing it get no resource (RFC 4918 9.10.4) | yes | yes |
| 11 | Tagged `If: <http://host/dav/…> (<token>)` → `KeyNotFoundException` → 500 | Any client sending tagged If headers (litmus/neon do; Office not verified) gets 500 on writes to locked files | yes | yes |
| 12 | lockdiscovery token is `urn:uuid:urn:uuid:…` | Clients cannot match the token | yes | yes |
| 13 | A token is only accepted when tagged with the exact request URL; COPY of a locked source needs a token; refresh through a member fails | Collection locks are unusable | yes | yes |
| 14 | DELETE of a missing resource → 204; MKCOL/PUT below a missing parent → 404 (not 409); MKCOL with a body is not refused (415) | Small RFC violations | yes | yes |
| 15 | PROPFIND with malformed XML is treated as allprop (207, not 400) | Small (not fixed in the patch) | yes | yes |

### What the store or our middleware has to do (no library change)

- **OPTIONS:** the library always answers `DAV: 1, 2` with every method and
  no `MS-Author-Via`. Our gate answers OPTIONS itself, so read-only can
  advertise class 1.
- **Read-only mode:** our gate refuses write methods before the library sees
  them.
- **Decode `LocalPath` once:** the library passes `file://` URIs whose
  `LocalPath` keeps `#` and `%` escaped.
- **Keep the client's spelling of each segment** in the URIs we return. The
  library compares segments case-sensitively after asking the store, so
  returning canonical names breaks case-insensitive access.
- **Collections need a trailing slash** in their `Uri`, so that PROPFIND
  hrefs of folders end with `/`.

### Risks the PoC could not test

- **Windows Explorer itself:**
  - whether it probes `OPTIONS /` and `PROPFIND /`;
  - Basic over HTTPS;
  - the 50 MB download limit;
  - Office open and save.
  These still need the manual checklist in the plan.
- **`new Uri("/path")`** is how the library represents paths. On Linux it
  becomes a `file://` URI. On Windows, a rooted path without a drive letter
  is not a valid URI, so the library will probably throw when PaperDotNet
  itself runs on Windows (developer machines). Not tested. When vendoring,
  replace `Uri` with a path type, or with `new Uri(path, UriKind.Relative)`
  plus a base.
- **Concurrency:** the library is stateless per request, apart from the
  static property registry. Locks need the EF lock manager to be multi-node
  safe. Not exercised here.

## What this means for the plan

- **Vendoring is confirmed as the right call:** the package needs about 15
  fixes, two of them blocking (MOVE, PUT status). Upstream has had one
  commit since 2023.
- **WD-0 grows:** apply `library-fixes.patch` plus the `Uri`/path cleanup,
  and port litmus into CI as the conformance test. The goal is litmus basic,
  copymove, locks and http at 100%, and props apart from dead properties.
- **WD-1 (read-only) needs fixes 2, 5 and 6** (escaping, ETag, ranges). It
  also needs the OPTIONS gate and the store rules above.
- **WD-3 depends on fix 1** (MOVE through the store) for identity-keeping
  renames and the Office safe-save rule. It also depends on fixes 3, 7, 10
  and 11 for saving and locking.
