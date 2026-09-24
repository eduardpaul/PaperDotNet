# Modern .NET building blocks for PaperDotNet

**Status:** analysis, for review (2026-09-24)
**Scope:** backend API only (see [architecture-vision.md](architecture-vision.md))

This doc lists the platform features and libraries in modern .NET that fit the
architecture and the [ideas](../ideas/README.md). Each entry says what it would
be used for in PaperDotNet.

Every library here passes the license policy in
[dependency-licenses.md](dependency-licenses.md): MIT or Apache-2.0, or an
equivalent permissive license. Copyleft and commercial libraries were removed.

Legend for the **Use** column:

- ✅ **adopt**: use from the start
- 🟡 **evaluate**: likely useful, prove it with a spike first
- ⏳ **later**: useful in a later phase

---

## 0. Target framework

| Choice | Recommendation |
|---|---|
| **.NET 10 (LTS, C# 14)** | ✅ Target this. It is supported until Nov 2028. |
| .NET 11 (STS) | ⏳ It ships Nov 2026 and is at RC1 today. Upgrade only when a feature needs it. It adds native OpenTelemetry tags in ASP.NET Core, async validation and OpenAPI 3.2 for Minimal APIs, and Zstandard compression. The next LTS, .NET 12, ships Nov 2027. |

---

## 1. Hosting, composition & configuration

| Building block | Use | Where it fits in PaperDotNet |
|---|---|---|
| **Generic Host** (`Microsoft.Extensions.Hosting`) | ✅ | One host model for the API, the workers (OCR, indexing) and the extension sidecar supervisor |
| **Dependency injection + keyed services** (.NET 8+) | ✅ | Registries for pluggable implementations resolved by key: field types (`"text"`, `"money"`), storage providers, search providers and auth providers |
| **Options pattern + validation** (`ValidateOnStart`, source-generated validators) | ✅ | Typed settings. Per-tenant options are resolved via `ITenantContext` (idea 0005) |
| **`BackgroundService` / `IHostedService`** | ✅ | Outbox dispatcher, schedulers, sidecar supervisor, reindex jobs |
| **.NET Aspire** | ✅ (dev) / 🟡 (deploy) | Local orchestration of the API, PostgreSQL, the OCR worker container, Node/Python extension sidecars and object storage (SeaweedFS, Apache-2.0, or the local-disk provider). Its dashboard shows OpenTelemetry out of the box. Also used for integration tests (`Aspire.Hosting.Testing`) |
| **Feature management** (`Microsoft.FeatureManagement`) | 🟡 | Per-tenant feature flags and staged rollout of extensions |

## 2. Concurrency, streaming & in-process pipelines

| Building block | Use | Where it fits |
|---|---|---|
| **`System.Threading.Channels`** | ✅ | The in-process work pipe. It provides bounded queues with backpressure between stages:<br>• outbox dispatcher → event handlers (idea 0012)<br>• file-processing pipeline (upload → MIME detection → thumbnail → OCR → indexing)<br>• webhook sender<br>• bulk import<br>Channels are in memory, so durability comes from the outbox table, and channels just feed workers |
| **`IAsyncEnumerable<T>`** | ✅ | Streaming large result sets and exports from EF Core straight to the response. Also delta sync feeds (ideas 0003, 0006) |
| **`System.IO.Pipelines` / streams** | ✅ | Streaming uploads and downloads with no full buffering: large PDFs, and pass-through to S3 |
| **`Parallel.ForEachAsync`** | ✅ | Bounded parallelism for batch jobs, e.g. re-OCR or reindexing a tenant |
| **`TimeProvider` + `FakeTimeProvider`** | ✅ | Testable time for reminders, recurrence, due dates, token expiry and retention |
| **`PeriodicTimer`** | ✅ | Lightweight schedulers inside hosted services |
| **`System.Threading.RateLimiting`** | ✅ | Per-tenant quotas on API calls, OCR jobs, webhooks and AI calls (idea 0005) |
| **`FrozenDictionary` / `FrozenSet`** | ✅ | Read-mostly registries built once at startup: field types, scopes, extension contributions |

## 3. Web API (ASP.NET Core)

| Building block | Use | Where it fits |
|---|---|---|
| **Minimal APIs + route groups + endpoint filters** | ✅ | Each module and each extension maps its own route group (`/v1.0/...`, `/ext/{id}/...`). Filters handle tenant, scope checks and ETags |
| **Built-in OpenAPI** (`Microsoft.AspNetCore.OpenApi`, OpenAPI 3.1 in .NET 10) | ✅ | Produces the OpenAPI document that **Kiota** turns into the TypeScript, Python and C# SDKs (idea 0003). Document transformers add extension endpoints |
| **Built-in Minimal API validation** (.NET 10, `AddValidation()`) | ✅ | Request DTO validation without a third-party library |
| **ProblemDetails** (RFC 9457) | ✅ | One error format, used for canceled before-events (idea 0012) and validation errors. Graph-style error envelopes can be mapped on top |
| **`Microsoft.AspNetCore.OData` 9.x** (Minimal API support since 9.4, `$batch`) | 🟡 | Graph-style `$filter/$select/$expand/$orderby/$top/$count` and `$batch` (idea 0003). Needs a spike with dynamic (JSON) fields. The alternative is a custom Graph-subset parser that emits LINQ expressions |
| **`Asp.Versioning.Http`** | 🟡 | `/v1.0` and `/beta` endpoints |
| **Rate limiting middleware** | ✅ | Partitioned by tenant and token |
| **HybridCache** (`Microsoft.Extensions.Caching.Hybrid`) | ✅ | A two-level cache (in memory, plus a Redis-protocol server when present: Garnet, MIT) with stampede protection and tag invalidation. Used for schema, content-type and term-store lookups, with keys prefixed by tenant |
| **Output caching** | 🟡 | Thumbnails and page images |
| **Server-Sent Events** (`TypedResults.ServerSentEvents`, .NET 10) | ✅ | Simple one-way live events: OCR status, job progress, change notifications |
| **SignalR** | ⏳ | Two-way real-time, once there is a UI |
| **Health checks** | ✅ | `/health/live` and `/health/ready` covering PostgreSQL, storage, the OCR worker and sidecars |
| **Response compression** (Brotli; Zstandard in .NET 11) | ✅ | JSON responses |
| **tusdotnet** (tus resumable upload protocol) | 🟡 | Resumable uploads for large files and mobile (idea 0006) |

## 4. Identity, security & multitenancy

| Building block | Use | Where it fits |
|---|---|---|
| **JWT bearer + OpenID Connect handlers** | ✅ | API authentication. Each tenant can have its own identity provider (idea 0005) |
| **OpenIddict** (Apache-2.0) | 🟡 | A self-hosted OAuth2/OIDC server for local accounts, API tokens and MCP OAuth (idea 0004). It avoids a separate auth server like Papermerge's. Any standard OIDC provider (e.g. Keycloak, Apache-2.0) stays supported as external provider |
| **ASP.NET Core Identity** | 🟡 | User store for local accounts (pairs with OpenIddict) |
| **Authorization policies + custom `IAuthorizationPolicyProvider`** | ✅ | Dynamic scopes (`document.download`, extension-declared scopes) become policies on demand. Resource-based handlers check item ACLs |
| **Data Protection API** | ✅ | Encrypting secrets such as webhook secrets, per-tenant OIDC secrets and extension settings. Keys stored in PostgreSQL through EF Core |
| **`Microsoft.Extensions.Compliance.Redaction`** | 🟡 | Redacting personal data in logs and audit output |
| **Multitenancy library** (e.g. Finbuckle.MultiTenant) | 🟡 | Covers tenant resolution strategies and per-tenant options/auth. The alternative is a small in-house `ITenantContext`. The core rules (EF filters, interceptor) stay ours either way |

## 5. Data access (EF Core 10 + PostgreSQL)

| Building block | Use | Where it fits |
|---|---|---|
| **EF Core 10 named query filters** | ✅ | Multiple global filters per entity that can be switched off one at a time: `Tenant` (never disabled in normal code) and `SoftDelete` (disabled for the recycle bin and admin). This is exactly what ideas 0005 and 0010 need |
| **EF Core 10 complex types mapped to JSON** (incl. `ExecuteUpdate` inside JSON) | ✅ | Item field values stored as a JSON column. This stays portable, which matches the decision to be able to switch databases later |
| **`SaveChanges` interceptors** | ✅ | Stamp `TenantId` and audit columns, write outbox events, capture item versions (idea 0010), run before-event handlers (idea 0012) |
| **`ExecuteUpdate` / `ExecuteDelete`** | ✅ | Bulk operations such as term merges (idea 0008) and retention trimming |
| **Compiled models / compiled queries** | ⏳ | Startup and hot-path speed-ups |
| **Npgsql EF Core provider** | ✅ | `jsonb`, GIN indexes, full-text search (`EF.Functions.ToTsVector`, `WebSearchToTsQuery`) and arrays. Kept in the PostgreSQL project only |
| **PostgreSQL `ltree`** (via Npgsql) | 🟡 | Fast subtree queries for folder paths and taxonomy term hierarchies (idea 0008). It is PostgreSQL-specific, so it sits behind a query abstraction |
| **PostgreSQL row-level security** | ✅ | Second line of defense for tenant isolation |
| **EF Core migrations bundles** | ✅ | Ship migrations as a single executable in the deploy pipeline |

## 6. Background jobs, messaging & the outbox

| Building block | Use | Where it fits |
|---|---|---|
| **Own outbox + Channels** | ✅ | The simplest start. The outbox table is written in the same `SaveChanges`. A hosted dispatcher reads it and pushes the events into Channels, and consumers (async after-events, webhooks, indexing, automation) read from there |
| **Wolverine** (MIT) | 🟡 | A durable EF Core/PostgreSQL outbox, local queues, retries, scheduled messages and a message bus. It could replace the hand-made outbox |
| **Quartz.NET** (Apache-2.0) | 🟡 | Cron-style schedules such as reminders, recurring tasks and retention. Has a clustered PostgreSQL job store. (Hangfire excluded: LGPL-3.0 core) |
| **Elsa Workflows 3** | ⏳ | The automation engine (idea 0009). It consumes after-events from the outbox |

## 7. Extensibility runtime

| Building block | Use | Where it fits |
|---|---|---|
| **`AssemblyLoadContext`** (isolated, collectible) | ✅ | Load .NET extensions each in their own context. They share contract assemblies (`PaperDotNet.Abstractions`) with the host, and unloading allows hot upgrade |
| **Source generators + Roslyn analyzers** | 🟡 | The extension SDK generates manifest and registration code from attributes (`[FieldType]`, `[ItemEventHandler("ItemAdding")]`, `[McpTool]`). Analyzers catch misuse at compile time |
| **`System.Text.Json`** (source-generated, `JsonSchemaExporter` .NET 9+) | ✅ | Serialization, and JSON Schema generated for field types, extension settings and content types. The same schemas feed the manifest, OpenAPI, MCP tools and the future UI |
| **gRPC** (`Grpc.AspNetCore`) or **StreamJsonRpc** | 🟡 | The protocol between the host and Node/Python extension sidecars (idea 0011), over Unix sockets or named pipes. gRPC gives strict contracts for many languages. StreamJsonRpc (Microsoft's JSON-RPC, used by Visual Studio) is lighter and closer to MCP/VS Code style |
| **Kiota** | ✅ | Generates client SDKs, including the SDKs Node/Python extensions use to call back into the API (ideas 0003, 0011) |

## 8. AI building blocks

| Building block | Use | Where it fits |
|---|---|---|
| **`Microsoft.Extensions.AI`** (GA): `IChatClient`, `IEmbeddingGenerator`, middleware for function calling, caching, telemetry and rate limits | ✅ | A provider-neutral AI layer: Anthropic, OpenAI, Azure, and Ollama for local models. Used for auto-tagging and classification (ideas 0008, 0009), metadata extraction into custom fields (structured output), summaries and Q&A over documents. Extensions get an `IChatClient` from the host, so they don't bring their own providers. The model is configurable per tenant |
| **`Microsoft.Extensions.VectorData`** (GA) + PostgreSQL pgvector connector | 🟡 | Semantic and hybrid search next to full-text search, behind the `ISearchProvider` abstraction. Vector stores are swappable (pgvector, Qdrant) |
| **`Microsoft.Extensions.DataIngestion`** (preview) | 🟡 | Document → chunks → embeddings → vector store pipeline. It fits the file-processing pipeline right after OCR. Wait for GA or wrap it |
| **MCP C# SDK** (`ModelContextProtocol.AspNetCore`, 1.0 since Feb 2026, now 2.x) | ✅ | The built-in MCP server (idea 0004), hosted in the API with streamable HTTP. Tools come from the lists engine plus extension-contributed `[McpTool]`s. Authorization reuses our OAuth scopes |
| **Microsoft Agent Framework 1.0** (GA Apr 2026, successor of Semantic Kernel + AutoGen) | ⏳ | Multi-step agents, e.g. an "inbox triage" agent that classifies, tags, files and creates tasks. Only needed beyond single `IChatClient` calls |
| **`Microsoft.ML.Tokenizers`** / **ONNX Runtime** | ⏳ | Token counting for chunking, and local on-device models such as embeddings or classifiers for air-gapped installs |

## 9. Observability & resilience

| Building block | Use | Where it fits |
|---|---|---|
| **OpenTelemetry** (built-in `ActivitySource`/`Meter`) | ✅ | Traces across the API, outbox, workers, sidecars and AI calls, tagged with tenant and extension id. Exported to the Aspire dashboard in dev and any OTLP backend in prod |
| **`[LoggerMessage]` source-generated logging** | ✅ | Fast structured logs |
| **`Microsoft.Extensions.Http.Resilience`** (Polly v8) | ✅ | Retries, timeouts and circuit breakers for webhooks, remote extensions, AI providers and S3 |

## 10. Documents, files & domain libraries

| Library | License | Use | Where it fits |
|---|---|---|---|
| **PdfPig** | Apache-2.0 | ✅ | PDF text and page count extraction |
| **PDFsharp** | MIT | ✅ | Page operations: delete, reorder, rotate, merge, extract |
| **PDFtoImage** or **Docnet.Core** (both bundle PDFium) | MIT (PDFium: BSD-3) | 🟡 | Rendering PDF pages to images for thumbnails and OCR input |
| **SkiaSharp** | MIT (Skia: BSD-3) | 🟡 | JPEG/PNG decoding, resizing thumbnails |
| **LibTiff.Net** | BSD-3 | 🟡 | Multi-page TIFF reading, TIFF → PDF |
| **Tesseract** engine + `Tesseract` NuGet wrapper (container) | Apache-2.0 (Leptonica: BSD-2) | ✅ | OCR worker. Tesseract outputs a text-only PDF layer that PDFsharp merges into the new version |
| **AWSSDK.S3** | Apache-2.0 | ⏳ | S3 and R2 storage provider |
| **MailKit / MimeKit** | MIT | ⏳ | Email to inbox (idea 0002): IMAP polling and MIME parsing |
| **Ical.Net** | MIT | ✅ (phase 4) | RRULE recurrence expansion, and iCal import/export for Calendar |
| **Markdig** | BSD-2 | ⏳ | Markdown notes (idea 0007) |
| **System.CommandLine 2.0** (GA Nov 2025) | MIT | ✅ | Server admin CLI: tenants, users, reindex, migrations |

## 11. Testing

| Building block | Use | Where it fits |
|---|---|---|
| **xUnit v3** + **`WebApplicationFactory`** | ✅ | Unit and API tests |
| **Testcontainers for .NET** (PostgreSQL) | ✅ | Real-database tests, needed for JSON, full-text search, RLS and tenant filters |
| **`Aspire.Hosting.Testing`** | 🟡 | End-to-end tests of API + worker + sidecar |
| **Verify** (snapshot testing, MIT) | 🟡 | Tests that the OpenAPI document, manifests and API responses don't change unexpectedly |
| **`FakeTimeProvider`** | ✅ | Time-dependent logic |

## 12. Excluded because of their license

See [dependency-licenses.md](dependency-licenses.md) for the full register.

| Excluded | License | Use instead |
|---|---|---|
| MediatR, AutoMapper | Commercial since 2025 | Plain handlers + DI; Mapperly (Apache-2.0) |
| MassTransit v9 | Commercial | Wolverine (MIT) or own outbox + Channels |
| FluentAssertions v8 | Commercial | AwesomeAssertions (Apache-2.0) or xUnit asserts |
| Shouldly | BSD-3 (permissive, but not needed) | AwesomeAssertions |
| ImageSharp | Six Labors split license | SkiaSharp (MIT) |
| iText, QuestPDF | AGPL / commercial; community license with revenue limit | PDFsharp (MIT), PdfPig (Apache-2.0) |
| Magick.NET | Apache-2.0 wrapper, but bundles LGPL native delegates (e.g. libheif, libde265) | SkiaSharp + LibTiff.Net + PDFsharp |
| OCRmyPDF, img2pdf | MPL-2.0 / LGPL-3.0; OCRmyPDF needs Ghostscript (AGPL) | Tesseract (Apache-2.0) + PDFsharp |
| Hangfire | LGPL-3.0 core, commercial Pro | Quartz.NET (Apache-2.0), Wolverine (MIT) |
| Mime-Detective | Modified MIT with a redistribution restriction | Own magic-byte sniffer, or `Mime` (MIT) |
| Scriban | BSD-2 (permissive, but not needed) | Fluid (MIT) |
| Redis 7.4+ | RSALv2 / SSPL / AGPL | Garnet (MIT), Valkey (BSD-3) |
| MinIO | AGPL-3.0 | Local-disk provider; SeaweedFS (Apache-2.0) for S3 tests |
| Elasticsearch | AGPL / SSPL / Elastic License | OpenSearch (Apache-2.0), Meilisearch (MIT) |
| Zitadel (as a recommended IdP) | AGPL-3.0 | Keycloak (Apache-2.0), OpenIddict (Apache-2.0). Any OIDC provider still works through the standard protocol |

---

## Recommended baseline for phase 0 (foundation)

Self-hosting and practicality come first (decided 2026-09-24). The baseline
runs as **one PaperDotNet container + PostgreSQL**.

"Simple" means **practical**: the least effort to build, run and maintain.

- **Extra services** (servers, brokers, workers) cost every self-hoster, so they stay optional.
- **In-process libraries** that run inside the app and store data in PostgreSQL are welcome when they save us code.
- **Never hand-roll** security, protocol or file-format code.

**Required at runtime:** PostgreSQL. Nothing else.

**Optional, off by default:**
- Garnet or another Redis-protocol cache (HybridCache works in memory without it)
- S3 storage (the default is a local volume)
- an external OIDC provider (the default is built-in local accounts)
- external search or vector engines (the default is PostgreSQL full-text search and pgvector)
- a separate OCR worker (the default is Tesseract in the main image)
- AI providers

**Practical choices:**

| Concern | Choice | Why |
|---|---|---|
| Scheduling (reminders, recurrence, retention) | **Quartz.NET** (in-process, PostgreSQL job store) | Mature. Writing our own cron/misfire/cluster handling isn't worth it |
| Outbox + event dispatch | **Own outbox table + Channels**; switch to **Wolverine** if retries, dead-lettering and scheduling start to grow | The basic version is small and core to the event-handler design (idea 0012). Wolverine is the fallback before we reinvent a message bus |
| Auth server (local accounts, API tokens, MCP OAuth) | **OpenIddict** + ASP.NET Core Identity | OAuth/OIDC must not be hand-rolled, and it needs no extra container |
| Tenant resolution + per-tenant options/auth | **Finbuckle.MultiTenant** unless the spike shows it gets in the way of our EF rules | It saves writing resolution strategies and per-tenant auth plumbing. Tenant isolation rules in EF stay ours |
| Graph-style `$filter`/`$select`/`$batch` | **Decide by spike:** OData library vs own subset parser | Pick whichever needs less code for JSON-backed dynamic fields |
| PDF / image / OCR | PDFsharp, PdfPig, PDFium, SkiaSharp, Tesseract | Never hand-roll file formats |
| Dev orchestration | Aspire **for development only** | The shipped artifact is a Docker image + `docker-compose.yml` |

Baseline:

- **Runtime and hosting:** .NET 10, the Generic Host, and Aspire for local development only.
- **Web API:** ASP.NET Core Minimal APIs, built-in OpenAPI and validation, ProblemDetails, rate limiting, health checks.
- **Data:** EF Core 10 with Npgsql, using named query filters (tenant, soft delete) and interceptors (tenant, audit, outbox).
- **Background work:** an outbox table, a hosted dispatcher, `System.Threading.Channels`, and Quartz.NET for schedules.
- **Security:** JWT/OIDC authentication and dynamic authorization policies for scopes.
- **Caching and resilience:** HybridCache (in memory), OpenTelemetry (exporter optional), and `Http.Resilience`.
- **Auth:** OpenIddict + Identity for built-in local accounts and API tokens, so no external IdP is needed. An external OIDC provider is optional.
- **Testing:** xUnit v3, Testcontainers, and `FakeTimeProvider`.

Spikes to run before the phases that need them:

1. **OData vs a custom Graph-style query parser** over JSON fields (idea 0003).
2. **`AssemblyLoadContext` extension loading**, including unload and shared contracts (phase 2).
3. **Sidecar RPC:** gRPC vs StreamJsonRpc, and before-event latency (ideas 0011, 0012).
4. **Our own outbox vs Wolverine.** Measure how much code our own needs for retries, dead-letter and delays.
5. **OpenIddict** setup for local accounts, API tokens and MCP OAuth.
7. **Finbuckle.MultiTenant** together with our EF Core tenant filters.
6. **Microsoft.Extensions.AI:** auto-tagging from OCR text with structured output (ideas 0008, 0009).

## Sources

- [AI and Vector Data Extensions are now GA (.NET Blog)](https://devblogs.microsoft.com/dotnet/ai-vector-data-dotnet-extensions-ga/)
- [Microsoft.Extensions.VectorData (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/mevd-library)
- [Data ingestion building blocks (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/data-ingestion)
- [Microsoft Agent Framework 1.0 (Visual Studio Magazine)](https://visualstudiomagazine.com/articles/2026/04/06/microsoft-ships-production-ready-agent-framework-1-0-for-net-and-python.aspx)
- [Release v1.0 of the official MCP C# SDK (.NET Blog)](https://devblogs.microsoft.com/dotnet/release-v10-of-the-official-mcp-csharp-sdk/)
- [EF Core 10: named query filters, JSON complex types](https://www.ludmal.com/blog/ef-core-10-features)
- [.NET 11 Preview 6 roundup (Visual Studio Magazine)](https://visualstudiomagazine.com/articles/2026/07/15/net-11-preview-6-roundup-aspnet-core-maui-c-ef-core-and-sdk-updates.aspx)
- [What's new in .NET 11 (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [OData on ASP.NET Core Minimal APIs (OData blog)](https://devblogs.microsoft.com/odata/enable-odata-functionalities-on-asp-net-core-minimal-api/)
- [System.CommandLine 2.0.0 (NuGet)](https://www.nuget.org/packages/System.CommandLine/2.0.0)
