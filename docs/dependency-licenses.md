# Dependency license register

**Status:** checked 2026-09-24 against the NuGet registry (`licenseExpression`
of the latest published version) and the projects' license files.

## Policy

| Class | Licenses | Rule |
|---|---|---|
| ✅ **Allowed** | MIT, Apache-2.0 | Use freely |
| 🟨 **Allowed with notice** (decided) | BSD-2-Clause, BSD-3-Clause, PostgreSQL, ISC | Permissive with attribution only, equivalent to MIT. Prefer MIT/Apache when an equally simple option exists. Listed below so the choice is explicit |
| ⛔ **Not allowed** | GPL, AGPL, LGPL, MPL, SSPL, RSAL, BUSL, Elastic License, "source-available", commercial/dual licenses with revenue limits, and MIT/Apache with added restrictions | Do not add as a dependency, including bundled native libraries |

The policy applies to everything shipped or required to run PaperDotNet: NuGet
packages, native libraries they bundle, and required containers/services. It
does not apply to optional external systems reached only via a standard
protocol (e.g. any OIDC identity provider, any S3-compatible store). Those are
the operator's choice.

Enforce it in CI with a license check over the NuGet dependency graph (e.g. the
`nuget-license` tool) once the solution exists.

## Register: packages in the proposal

### ✅ MIT / Apache-2.0

| Package / component | License | Used for |
|---|---|---|
| .NET 10 runtime, ASP.NET Core, EF Core, `Microsoft.Extensions.*` (Hosting, DI, Options, Caching.Hybrid, Http.Resilience, Compliance.Redaction, TimeProvider.Testing) | MIT | Platform |
| `Microsoft.AspNetCore.OpenApi` | MIT | OpenAPI document |
| `Microsoft.AspNetCore.OData` | MIT | Graph-style query options (spike) |
| `Asp.Versioning.Http` | MIT | API versioning |
| `Microsoft.FeatureManagement.AspNetCore` | MIT | Feature flags |
| Aspire (`Aspire.Hosting`) | MIT | Local orchestration, tests |
| `tusdotnet` | MIT | Resumable uploads |
| `OpenIddict.AspNetCore` | Apache-2.0 | Built-in OAuth2/OIDC server |
| `Finbuckle.MultiTenant.AspNetCore` | Apache-2.0 | Tenant resolution (evaluate) |
| `WolverineFx` | MIT | Durable outbox / messaging (evaluate) |
| `Quartz` | Apache-2.0 | Scheduling |
| `Grpc.AspNetCore` | Apache-2.0 | Sidecar RPC (option) |
| `StreamJsonRpc` | MIT | Sidecar RPC (option) |
| Kiota (`Microsoft.Kiota.*`) | MIT | SDK generation |
| `Microsoft.Extensions.AI`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Extensions.DataIngestion` (preview) | MIT | AI building blocks |
| `Microsoft.Agents.AI` (Agent Framework) | MIT | Agents (later) |
| `ModelContextProtocol.AspNetCore` | Apache-2.0 | MCP server |
| `Microsoft.SemanticKernel.Connectors.PgVector`, `Pgvector.EntityFrameworkCore` | MIT | pgvector access |
| `Microsoft.ML.Tokenizers`, `Microsoft.ML.OnnxRuntime` | MIT | Tokenizing, local models |
| `OpenTelemetry` | Apache-2.0 | Tracing / metrics |
| `PdfPig` 0.1.16 | Apache-2.0 | PDF text layer per page (3b); PDF builder in tests |
| `PDFsharp` | MIT | Page operations, image → PDF, text-layer merge |
| `PDFtoImage` 5.4 (+ `bblanchon.PDFium.*` natives) | MIT (bundles PDFium, see below) | Page rendering for thumbnails, previews and OCR input (3b) |
| `SkiaSharp` (+ `SkiaSharp.NativeAssets.*`, via PDFtoImage) | MIT (bundles Skia, see below) | JPEG/PNG decode, resize, JPEG encode (3b) |
| Tesseract engine and tessdata (container package, run as CLI) | Apache-2.0 (bundles Leptonica, see below) | OCR (3b). No NuGet wrapper: the CLI via `CliWrap` |
| `AWSSDK.S3` | Apache-2.0 | S3 storage provider |
| `MailKit` / `MimeKit` | MIT | Email to inbox (idea 0002) |
| `Ical.Net` 5.2 | MIT | RRULE parsing and expansion (4a), iCal import/export (4b) |
| `NodaTime` (via Ical.Net) | Apache-2.0 | Time zones for Ical.Net |
| `Fluid.Core` | MIT | Path templates |
| `Mime` (HeyRed) | MIT (wraps libmagic, see below) | MIME sniffing (optional) |
| `Novell.Directory.Ldap.NETStandard` | MIT | LDAP (optional) |
| `System.CommandLine` | MIT | Admin CLI |
| `xunit.v3` | Apache-2.0 | Tests |
| `Testcontainers.PostgreSql` | MIT | Tests |
| `Verify.XunitV3` | MIT | Snapshot tests |
| `AwesomeAssertions` | Apache-2.0 | Test assertions (optional) |
| `Riok.Mapperly` | Apache-2.0 | Object mapping (optional) |
| `ClearScript`, `pythonnet` | MIT | Embedded runtimes (idea 0011, not recommended path) |
| Garnet (`Microsoft.Garnet`) | MIT | Optional Redis-protocol cache server |
| Keycloak | Apache-2.0 | Example external IdP |
| OpenSearch / Qdrant / Meilisearch Community | Apache-2.0 / Apache-2.0 / MIT | Optional external search/vector engines |
| SeaweedFS | Apache-2.0 | S3-compatible store for tests |
| Ollama | MIT | Optional local LLM runtime |
| `EFCore.NamingConventions` | Apache-2.0 | snake_case table and column names |
| `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.Data.Sqlite` | MIT | SQLite provider (default database, ADR-0009) |
| `SQLitePCLRaw.*` (via Microsoft.Data.Sqlite) | Apache-2.0 | Native SQLite bindings |
| SQLite (native library, bundled) | Public domain | Database engine |
| `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.IdentityModel.JsonWebTokens` | MIT | JWT access tokens (P0; removed in 1f, replaced by OpenIddict) |
| `OpenIddict.Server.AspNetCore`, `.Server.DataProtection`, `.Validation.AspNetCore`, `.Validation.DataProtection`, `.Validation.ServerIntegration`, `.EntityFrameworkCore` (+ `OpenIddict.Abstractions`, `.Core`, `.Server`, `.Validation`, `.EntityFrameworkCore.Models`) | Apache-2.0 | OAuth 2.0 / OpenID Connect server and token validation (1f, ADR-0013) |
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` | MIT | Data Protection key ring in the database (1f) |
| `System.Formats.Cbor` | MIT | Tests only: software WebAuthn authenticator |
| `Microsoft.CodeAnalysis.CSharp` | MIT | Build only: extension registration source generator and extension analyzers (ADR-0014, EXT-05) |
| `Microsoft.AspNetCore.Mvc.Testing` | MIT | Integration tests; extension test host (`PaperDotNet.Extensions.Testing`) |
| `xunit.v3.mtp-v2` | Apache-2.0 | Tests on Microsoft.Testing.Platform |
| `dotnet-ef` (local tool) | MIT | Migrations |
| `Microsoft.AspNetCore.OData` (+ `Microsoft.OData.Core`, `.Edm`, `.ModelBuilder`, `Microsoft.Spatial`) | MIT | OData query parsing for list items (ADR-0007) |
| `WolverineFx` (+ `.EntityFrameworkCore`, `.Postgresql`, `.Sqlite`, `.RuntimeCompilation`, `.RDBMS`) | MIT | Transactional outbox and durable local queues (ADR-0008) |
| `JasperFx` (+ `.Events`, `.RuntimeCompiler`, `.SourceGenerator`), `Weasel.*` (via Wolverine) | MIT | Wolverine code generation and database schema management |
| `Microsoft.CodeAnalysis.*` (Roslyn, via Wolverine runtime compilation) | MIT | Runtime compilation of message handlers |
| `FastExpressionCompiler`, `Spectre.Console`, `Newtonsoft.Json` (via Wolverine) | MIT | Wolverine dependencies |
| `Cronos` | MIT | Cron expressions for recurring jobs (ADR-0010) |
| `ModelContextProtocol.AspNetCore` (+ `ModelContextProtocol`, `ModelContextProtocol.Core`) | Apache-2.0 | MCP server for AI assistants (API-08/09); the official C# SDK |
| `Microsoft.Extensions.AI.Abstractions` | MIT | AI abstractions (`IEmbeddingGenerator`); also a dependency of the MCP SDK |
| `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI` (+ `OpenAI`, `System.ClientModel`) | MIT | Embedding providers for semantic search: any OpenAI-compatible API, e.g. Ollama or OpenAI (ADR-0027) |
| `System.Numerics.Tensors` | MIT | Vector similarity (`TensorPrimitives`) for semantic search |
| `Microsoft.Kiota.Bundle` (+ `.Abstractions`, `.Http.HttpClientLibrary`, `.Serialization.*`) | MIT | Runtime of the generated C# SDK (API-03) |
| `Microsoft.OpenApi.Kiota` (.NET tool) | MIT | Generates the SDKs; build time only |
| `@microsoft/kiota-bundle` (npm), `microsoft-kiota-bundle` (PyPI, with `httpx`: BSD-3-Clause) | MIT | Runtime of the TypeScript and Python SDKs (separate packages, not in the server) |
| `CliWrap` | MIT | Running the Tesseract CLI |
| `TngTech.ArchUnitNET` (+ `.xUnitV3`) | Apache-2.0 | Architecture tests |
| `OpenIddict.EntityFrameworkCore` | Apache-2.0 | OpenIddict stores |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | MIT | Identity stores |
| `Quartz.Extensions.Hosting`, `Quartz.Serialization.SystemTextJson` | Apache-2.0 | Quartz hosting |
| `OpenTelemetry.Extensions.Hosting`, `.Exporter.OpenTelemetryProtocol`, `.Instrumentation.AspNetCore` | Apache-2.0 | Telemetry |
| `Microsoft.Testing.Platform` | MIT | Test runner |

### 🟨 Permissive with notice (no MIT/Apache alternative, or core infrastructure)

| Component | License | Why it stays |
|---|---|---|
| **PostgreSQL** (incl. `ltree`, row-level security), **pgvector** extension | PostgreSQL License | The chosen database. The license is MIT-like |
| `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.OpenTelemetry` | PostgreSQL License | The only production-grade PostgreSQL provider for EF Core |
| `Polly.Core` (dependency of `Microsoft.Extensions.Http.Resilience`) | BSD-3-Clause | Pulled in by Microsoft's MIT resilience package |
| PDFium (bundled by PDFtoImage via `bblanchon.PDFium.*`; includes FreeType under its FTL option, libjpeg-turbo, OpenJPEG, lcms, zlib) | BSD-3-Clause (+ Apache-2.0 parts; bundled libraries BSD/FTL/IJG/zlib/MIT) | The only solid open PDF renderer |
| Skia (bundled by SkiaSharp) | BSD-3-Clause | Image decoding |
| Leptonica (bundled with Tesseract) | BSD-2-Clause style | Required by Tesseract |
| libmagic (bundled by `Mime`) | BSD-2-Clause | Only if `Mime` is used; our own sniffer avoids it |
| `LibTiff.Net` | BSD-3-Clause | Multi-page TIFF. Could be avoided by letting Tesseract read TIFFs directly |
| `Markdig` | BSD-2-Clause | Markdown parsing (idea 0007), the de-facto .NET standard |
| Valkey | BSD-3-Clause | Alternative to Garnet as a cache server (optional) |

### ⛔ Removed from the proposal

| Removed | License problem | Replacement |
|---|---|---|
| MediatR, AutoMapper | Commercial since 2025 | Plain handlers + DI, Mapperly |
| MassTransit v9 | Commercial | Wolverine, own outbox + Channels |
| FluentAssertions v8 | Commercial | AwesomeAssertions, xUnit asserts |
| ImageSharp | Six Labors split license | SkiaSharp |
| iText | AGPL / commercial | PDFsharp, PdfPig |
| QuestPDF | Community license with revenue limit | PDFsharp |
| Magick.NET | Bundles LGPL native delegates (libheif, libde265, …) | SkiaSharp + LibTiff.Net + PDFsharp |
| OCRmyPDF | MPL-2.0, requires Ghostscript (AGPL) | Tesseract + PDFsharp |
| img2pdf | LGPL-3.0 | PDFsharp |
| Hangfire | LGPL-3.0 core, commercial Pro | Quartz.NET, Wolverine |
| Mime-Detective | MIT with an added redistribution restriction | Own sniffer, or `Mime` |
| Scriban, Shouldly | BSD (permissive), but MIT/Apache alternatives exist | Fluid, AwesomeAssertions |
| Redis 7.4+ | RSALv2 / SSPL / AGPL | Garnet (MIT), Valkey (BSD-3) |
| MinIO | AGPL-3.0 | Local-disk provider, SeaweedFS |
| Elasticsearch | AGPL / SSPL / Elastic License | OpenSearch, Meilisearch |
| Zitadel as a recommended IdP | AGPL-3.0 | Keycloak, OpenIddict (any OIDC provider still works via the protocol) |
| Elsa Workflows 3.8+ | Depends on `JsonSchema.Net` 9 (json-everything), whose binaries come with an "Open Source Maintenance Fee" EULA (monthly fee for organizations with ≥ US$10k revenue). 3.7.1 is still clean but would be frozen, with about 60 packages including FastEndpoints, NSwag, pre-1.0 CShells and a second identity system | WorkflowCore (ADR-0018) |
| WorkflowCore (MIT) | License fine, but not needed: our messaging already gives durable waits and timers (ADR-0019). Kept on the branch `claude/automation-workflowcore` | Wolverine messages (ADR-0019) |
| `JsonSchema.Net` 8+/9+ and other json-everything packages | Binary releases under the Open Source Maintenance Fee EULA (revenue-dependent fee) | Not needed; avoid packages that depend on them |

## Decisions

- **2026-09-25:** no workflow engine dependency. Elsa was rejected because its current versions
  depend on a package whose binaries carry a revenue-dependent maintenance fee (ADR-0018).
  WorkflowCore was tried and then replaced by Wolverine messages we already use (ADR-0019).
- **2026-09-24:** the 🟨 permissive-with-notice class (BSD, PostgreSQL
  License, ISC) is **allowed**. Ship a `THIRD-PARTY-NOTICES` file with the
  required copyright notices.
- **2026-09-24:** self-hosting and practicality are the priority. Being on this
  list does not mean a package will be used. In-process libraries are
  welcome when they save code. Extra runtime services must stay optional (see the baseline in
  [dotnet-building-blocks.md](dotnet-building-blocks.md)). The only required
  runtime service is PostgreSQL.
