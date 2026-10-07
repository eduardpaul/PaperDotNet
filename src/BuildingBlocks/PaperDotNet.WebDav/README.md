# PaperDotNet.WebDav (vendored FubarDev.WebDavServer)

The WebDAV protocol for the `Dav` module ([ADR-0047](../../../docs/adr/0047-webdav-for-libraries.md),
[plan](../../../docs/webdav-plan.md)). Only the Dav module references this project and
`PaperDotNet.WebDav.Models` (architecture test).

## Upstream

- Project: [FubarDevelopment/WebDavServer](https://github.com/FubarDevelopment/WebDavServer), MIT
  ([LICENSE](LICENSE), © 2016 Fubar Development Junker).
- Branch `release/2.0`, commit `1f78db5d002793092d6fef4eda91006a2d56ab86` (2022-11-24; never released).
- Copied: `src/FubarDev.WebDavServer` → `Upstream/`, `src/FubarDev.WebDavServer.Models` →
  `../PaperDotNet.WebDav.Models/Upstream/`, and the MVC-free parts of `src/FubarDev.WebDavServer.AspNetCore`
  (`EscapedWebDavContext`, `WebDavResponse`, the context accessors, `WebDavHostOptions`) → `Upstream/AspNetCore/`.
- Upstream namespaces (`FubarDev.WebDavServer.*`) are kept so that upstream diffs stay readable.

## Rules for this folder

- Files under `Upstream/` are marked as generated code in `.editorconfig`: the solution's analyzers and
  `dotnet format` leave the upstream style alone. Each file starts with a `#nullable` line, so the compiler still
  checks nullability (`annotations` only for the files upstream generated from the XSD and resx).
- Every change to an upstream file carries a `PaperDotNet:` comment (`grep -rn "PaperDotNet:" Upstream`).
- New code outside `Upstream/` (`AspNetCore/`, `../PaperDotNet.WebDav.Models/Parsing/`) follows the solution's rules.

## Our changes

| Change | Why |
|---|---|
| Net10.0 only; `using Lock = FubarDev.WebDavServer.Locking.Lock` in five handlers; two `!` for .NET 10 annotations | `System.Threading.Lock` and new BCL annotations |
| Removed `Utils/UAParser` and `IWebDavContext.DetectedClient` | Unused; an embedded YAML parser we would have to maintain |
| Removed Scrutor, `System.Interactive.Async`, `FlakeyBit.DigestAuthentication`, `Microsoft.Extensions.*` 3.1 references | Explicit registrations (`AspNetCore/WebDavServiceCollectionExtensions.cs`), .NET 10 `AsyncEnumerable`, no Digest |
| Replaced the Yoakke-generated lexer/parser with `Parsing/HeaderParser.cs` (Models) | Six nightly prerelease packages for one header grammar; same tokens and rules, covered by the upstream tests |
| Replaced the MVC controller, XML input formatter and exception filter with `AspNetCore/WebDavEndpoints.cs` (`MapWebDav`) | No MVC in the host; XML bodies read with DTDs prohibited and a 1 MB limit |
| No server-to-server COPY/MOVE (the remote target factories are not registered) | The server never sends requests to hosts named in a `Destination` header |
| Only the escaped request context (`WebDavContextAccessor`) | The unescaped variant was a litmus switch |
| `IMovableCollection` + fast path in `CopyMoveHandlerBase` | Folder MOVE keeps the folder (id, permissions, values) instead of re-creating it |
| `TimeoutHeader.Parse`: comma lists, values above `Int32`, unknown units ignored | `Timeout: Infinite, Second-4100000000` was a 500 |
| `WebDavRequestHeaders`: malformed headers → 400; malformed conditional and `Timeout` headers ignored | Were 500s |
| HEAD sends `Content-Length`, `Content-Type` and the other entity headers of GET; GET sends `Content-Length` | Upstream sent none |
| German resources (`Resources.de.resx`) not compiled | Unused |

## Tests

The upstream unit tests of the library itself (header parsing, entity tags, ranges, URI comparison, converters)
are in `tests/PaperDotNet.UnitTests/WebDav`. Upstream's server tests drove the MVC controller through a WebDAV
client package; protocol behaviour is covered instead by the Dav integration tests
(`tests/PaperDotNet.IntegrationTests/Dav`) and litmus (`samples`-independent, see `docs/webdav.md`).
