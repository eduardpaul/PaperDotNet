# Spike: WebDAV with FubarDevelopment/WebDavServer, compared with Dav.AspNetCore.Server

The question: is [FubarDevelopment/WebDavServer](https://github.com/FubarDevelopment/WebDavServer)
a better base to fork than [Dav.AspNetCore.Server](https://github.com/ThuCommix/Dav.AspNetCore.Server)
(the library in [../webdav-poc](../webdav-poc/README.md),
[ADR-0047](../../docs/adr/0047-webdav-for-libraries.md))?

**How the comparison was made:**
- The same in-memory model and name rules (`PocModel.cs` and `DavNames.cs`
  are linked from the other spike).
- The same Basic auth with the token, valid only under `/dav`.
- The same Office safe-save rule.
- The same `check.py` (now 50 read-only and 76 read-write checks) and the
  same litmus suites.
- Linux, .NET SDK 10.0.401, litmus 0.13, 2026-10-06.
- **No real Windows client was used.**

## How to run

```bash
git clone -b release/2.0 https://github.com/FubarDevelopment/WebDavServer /tmp/fubar
./run.sh /tmp/fubar                                   # release/2.0 as is (net6.0 target, built by the .NET 10 SDK)
git -C /tmp/fubar apply "$PWD/fubar-folder-move.patch"
FOLDER_MOVE=true ./run.sh /tmp/fubar                  # with the folder-move fix
DEAD_PROPERTIES=false FOLDER_MOVE=true ./run.sh /tmp/fubar   # without a property store
```

`master` stopped in July 2019. **`release/2.0` has 200 more commits, up to
November 2022.** It never shipped to NuGet. That work includes a .NET 6
target, a rewritten `If` header parser, litmus compatibility work, the lock
owner and the `Lock-Token` handling. Everything below uses `release/2.0`.

## Results side by side

| | check.py read-only | check.py read-write | litmus basic | copymove | props | locks | http |
|---|---|---|---|---|---|---|---|
| Dav.AspNetCore.Server NuGet 1.0.1 | 46/50 | 56/76 | 14/16 | 12/13 | 9/14 | 9/13 (stops early) | 4/4 |
| Dav.AspNetCore.Server git HEAD | 48/50 | 59/76 | 14/16 | 12/13 | 9/14 | 9/13 (stops early) | 4/4 |
| Dav.AspNetCore.Server HEAD + our 15 fixes (+90/−30 lines) | **50/50** | **76/76** | 16/16 | 13/13 | 9/14 ¹ | 38/41 ¹ | 4/4 |
| **Fubar release/2.0, unchanged** | 46/50 | 73/76 | **16/16** | **13/13** | 22/30 ² | 38/41 | 4/4 |
| **Fubar + folder-move fix (+49 lines)** | 46/50 | **74/76** | 16/16 | 13/13 | 22/30 ² | 38/41 | 4/4 |
| Fubar + fix, without a property store | 43/50 | 71/76 | 16/16 | 13/13 | 11/14 | 35/41 | 4/4 |

¹ The remaining failures are dead properties, which we don't store.
² With the library's in-memory dead-property store. Both libraries fail the
same "propget" value checks, so we did not dig further.

The adapter fixes I made while getting the Fubar adapter right are not
counted as library changes. They are: live properties must implement
`ILiveProperty`; refuse existing names in `Create*Async`; check that a COPY
target is writable; allow OPTIONS anonymously through a policy.

**Folder listing performance:** an Explorer-style PROPFIND of the
5,000-document folder takes 0.54–0.63 s (5.3 MB). Dav.AspNetCore.Server
takes 0.44–0.54 s (5.0 MB).

## What Fubar gets right out of the box

These are the bugs we had to fix in Dav.AspNetCore.Server:

| Topic | Dav.AspNetCore.Server | Fubar release/2.0 |
|---|---|---|
| MOVE of a document | copy + delete, so identity is lost (blocking) | `IDocument.MoveToAsync`: identity kept |
| MOVE of a folder | copy + delete | **re-creates the folder** and moves the children, so the folder id, unique permissions and values are lost. Fixed by `fubar-folder-move.patch` (optional `IMovableCollection`). |
| PUT status | always 200; store errors (413/403/409) are lost (blocking) | 201/204; a `WebDavException` from the store or its write stream becomes the response status |
| Names with spaces, umlauts, `#`, `%` | broken in 1.0.1, fixed in HEAD | work |
| ETag header, `getetag` | unquoted | quoted (RFC 4918) |
| Range requests | off by one | correct |
| `Lock-Token` header, text `<owner>`, tagged `If` headers, lock refresh through members, COPY of locked resources | broken | work (litmus locks 38/41 before any change) |
| Timeout cap | ignored for `Infinite` | `MaxTimeout` works (`Second-7200` → 3600) |
| Read-only mode (`DAV: 1`) | not configurable; needed our OPTIONS gate | `EnableClass2 = false` |
| `MS-Author-Via: DAV` | missing | sent |
| Explorer's root probe | none | the sample ships `WinRootCompatController`; the docs tell you to test with Windows Explorer |
| Per-user file systems | none (one store, the user comes from `HttpContext`) | `IFileSystemFactory.CreateFileSystem(mountPoint, principal)` |
| Lock persistence | `ILockManager` (5 methods) | `LockManagerBase` with transactions, a cleanup task, a timeout policy. An EF lock manager is about 150 lines (like `InMemoryLockManager`). |
| Paths | `new Uri("/path")` (`file://`; probably throws on Windows hosts) | relative URIs |
| Tests | 1 small project | **282 unit tests, all passing on .NET 10** (run on net6.0 with roll-forward) |

## What is wrong or missing in Fubar

| # | Problem | Severity for us | Fix size |
|---|---|---|---|
| 1 | Folder MOVE re-creates the folder (see above) | **High**: folders carry permissions and values | 49 lines, done (`fubar-folder-move.patch`); its 282 tests still pass |
| 2 | `Timeout: Infinite, Second-4100000000` → 500 (the header parser does not trim after the comma) | **Possibly high**: we believe the Windows Mini-Redirector sends this form. Verify on Windows. | small |
| 3 | A conditional PUT with a lock token that does not exist on an unlocked resource succeeds (should be 412); litmus locks 18/20/22 | Medium: weaker lost-update protection | small to medium (`IfHeaderMatcher`) |
| 4 | HEAD sends no `Content-Length` | Low (Explorer reads sizes from PROPFIND) | small |
| 5 | PROPPATCH without a body → 500 (`NullReferenceException`), should be 400 | Low | small |
| 6 | In read-only mode, DELETE of a document answers 207 with a 403 inside, not 403; LOCK answers 501, not 405 | Low | small |
| 7 | `getcontenttype` and `displayname` are dead properties. Without any property store they disappear and GET sends `application/octet-stream`. | Design: we need an empty, write-ignoring `IPropertyStore` (no table), so the `IMimeTypeDetector` fallback answers (shown to work). | about 60 lines, not tested |
| 8 | A malformed `If-None-Match` → 500 instead of being ignored | Low | small |

## Cost of forking Fubar

- **Size:** about 28,000 lines (core 22,100, models 3,400, ASP.NET Core
  2,100) plus 6,400 lines of tests. Dav.AspNetCore.Server is about 3,000
  lines. There is much more code to own, but much less of it has to be
  fixed.
- **Retargeting to `net10.0` gives 16 compile errors**, all mechanical:
  - the class `Lock` clashes with `System.Threading.Lock`;
  - `System.Linq.Async` 4.0 clashes with .NET 10's `AsyncEnumerable`
    (drop the package);
  - one removed `Uri` constructor.
  We would also drop the `netstandard2.0`/`net461`/`netcoreapp3.1` targets.
- **Dependencies:** all are allowed by our license policy. A fork would
  remove most of them:

  | Package | License | In a fork |
  |---|---|---|
  | Yoakke.Lexer/Parser(.Generator) `2022.1.5-…-nightly` | Apache-2.0 | Used only for the `If` header grammar, and it is a **nightly prerelease**. Check in the generated parser, or pin it. |
  | Scrutor 4.2 | MIT | Assembly scanning for handler registration: replace with explicit registrations (also helps trimming and AOT) |
  | System.Interactive.Async / System.Linq.Async 4.0 | Apache-2.0 / MIT | Remove: built into .NET 10 |
  | FlakeyBit.DigestAuthentication.AspNetCore | MIT | Remove (we use our own Basic handler) |
  | Microsoft.Extensions.* 3.1 | MIT | Shared framework |
  | Tests: idunno.Authentication.Basic (Apache-2.0), MimeKitLite (MIT), **PortableWebDavLibrary** (license not verified) | – | Check, or replace PortableWebDavLibrary, before porting the tests |

- **Hosting model:** the endpoint is an **MVC Core controller**
  (`WebDavControllerBase`, `AddMvcCore`). PaperDotNet uses Minimal APIs.
  [ADR-0014](../../docs/adr/0014-build-time-extensions.md) keeps the door
  open for trimmed and Native AOT hosts, and MVC controllers do not support
  Native AOT. The core library only needs `IWebDavDispatcher` and
  `IWebDavResult`, so a fork can replace the 2,100-line ASP.NET Core layer
  with one Minimal API endpoint (moderate work). Until then, `AddMvcCore`
  works (shown here).
- **Upstream:** 108 stars, 37 forks, one main author. There have been no
  commits since November 2022, and 2.0 was never released. **Plan on owning
  the fork entirely**, as with the other library.

## Verdict

**Fubar `release/2.0` is the better base for the fork.**

- **It is better where it matters most:**
  - protocol correctness, shown by litmus and the Explorer-style checks
    before any change;
  - writes and locking: identity-keeping document moves, correct PUT
    status, a complete `If` and lock implementation, timeouts;
  - Windows awareness;
  - a real test suite.
- **What it needs is mostly porting and cleanup**, not protocol repair:
  - retarget to .NET 10 and remove dependencies;
  - optionally replace the MVC layer;
  - a 49-line folder-move fix;
  - a handful of small fixes (#2–#8).
- **Dav.AspNetCore.Server is much smaller and plain middleware**, which is
  attractive. But it needed 15 protocol fixes to reach the same point,
  including two blocking ones, and its fixes are only covered by this spike.

### If we switch, changes to the plan and ADR-0047

1. **The vendored base** becomes FubarDev.WebDavServer `release/2.0` (commit
   `1f78db5`): `FubarDev.WebDavServer`, `.Models`, and the core of
   `.AspNetCore`.
   - It goes into `src/BuildingBlocks/PaperDotNet.WebDav*` with the MIT
     notice.
   - The SQLite, TextFile and DotNet file system projects and the remote
     copy/move engine are not taken.
2. **WD-0 work:**
   - net10.0 only;
   - remove Scrutor, System.Interactive.Async and Digest;
   - deal with Yoakke (check in the generated parser or pin it);
   - port the 282 tests to xunit v3;
   - fixes #1–#8;
   - litmus in CI.
   - Decide on Minimal API versus `AddMvcCore` (recommended: Minimal API).
3. **The Dav module implements:**
   - `IFileSystemFactory`, per user, over Lists/Documents;
   - `IEntityTagEntry` (ETag from hash and version);
   - `IMimeTypeDetector` (the stored media type);
   - an empty `IPropertyStore`;
   - `IMovableCollection`;
   - an EF `LockManagerBase`.
   Uploads are a write stream that commits on dispose (spool, hash, version);
   errors are thrown as `WebDavException`.
4. **The name rules, Basic auth, read-only mode and the Office safe-save rule
   stay as planned.** All of them are shown in this adapter.
