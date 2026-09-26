# ADR-0032: Generated SDKs good enough for our own frontend

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

The SDKs (API-03, ADR-0021) are generated with Kiota from `sdk/openapi.json`.
The web UI will be built on the TypeScript SDK ("eat your own dog food"), so
anything a frontend needs must be reachable through it. Checking the generated
TypeScript client against the API showed gaps:

- **Undescribed inputs:** OData options (`$filter`, `$orderby`, `$top`,
  `$skiptoken`, …) are read from the query string, so they were missing from
  the generated request builders. Handlers that read raw JSON (merge patches,
  settings) had no documented body.
- **Errors:** only some responses were described, and not all in the same
  shape. Kiota throws a typed error only for described responses.
- **Shapes generators get wrong:**
  - `JsonObject`/`JsonElement` became empty models.
  - Numbers were `integer | string` with a pattern, so they became untyped.
  - Form uploads (`allOf` + `IFormFile`) did not become a multipart body.
  - Sibling routes with differently named parameters produced templates like
    `{%2Did}`.
  - Files and event streams were described as JSON.
- **Beyond any generator:**
  - Response headers are not typed, so `ETag` was unreachable.
  - Browser sign-in (authorization code + PKCE, refresh) and server-sent
    events are not generated.

## Decision

**1. The OpenAPI description is complete, and the server makes it so.** In
`PaperDotNet.Api` and `SdkOpenApiTransformers` (host):

- **Query options:** endpoints declare them with
  `.WithQueryOptions(QueryOptions.Items | Paging | Delta | Top | Skip …)`.
  `Page<T>` results get paging automatically.
- **Raw-JSON bodies:** handlers that read raw JSON document their body with
  `.WithRequestBodySchema<T>()`.
- **Files and event streams:** `.ProducesBinary(types)` for file downloads,
  `.ProducesEventStream()` for server-sent events.
- **Errors:** every 4xx/5xx response, plus `4XX`/`5XX` ranges, is an
  `ApiProblem`: RFC 9457 with `code` and `errors`.
- **Generator-friendly shapes:**
  - `JsonObject` is an object with any properties; SDKs expose it as a
    dictionary (`additionalData`).
  - `JsonElement` is inlined as any JSON (an untyped node).
  - Numbers are numbers only.
  - Multipart forms are flattened to one object with binary properties.
  - Unused component schemas are dropped.
- **Guarded by a test:** `SdkContractTests` fails when any of these regress.
  It covers paged responses without paging parameters, inconsistent sibling
  parameter names, string-typed numbers and a named `JsonElement`.

**2. ETags are in the bodies.** Every response for a resource protected with
`If-Match` carries `@odata.etag`, the same value as the `ETag` header, as
OData does. List entries carry one each. Clients never need response headers.

**3. A thin hand-written runtime, only for what cannot be generated.** It
lives in `sdk/typescript/src/runtime`:

| Helper | For |
|---|---|
| `OAuthSession` (oauth4webapi, MIT) | Authorization code + PKCE, refresh (single-flight, before expiry), password grant (scripts, tests), revoke + end-session |
| `createPaperDotNetClient` | The generated client with the token, the tenant, and one retry after a refreshed token on 401 |
| `problemOf`, `isStatus`, `validationErrors` | Reading the generated `ApiProblem` |
| `ifMatch(entity)` | `If-Match` from `@odata.etag` |
| `pages`, `all`, `toArray` | Following `@odata.nextLink` |
| `fields`, `fieldsOf`, `jsonOf`, `jsonNode` | Item values and any-JSON values as plain objects, both ways |
| `uploadBody`, `downloadFile` | Multipart bodies for the generated upload calls; downloads with file name and type |
| `subscribeLiveEvents` (`eventsource`, MIT) | `/v1.0/me/events` with the Authorization header and reconnects |
| `waitForOperation` | Polling long-running operations |

Everything else is generated, and the generated code is never edited.

**4. Dogfooding is tested.** `npm run test:e2e` (in `sdk/typescript`):
- builds and starts the real host on a free port with a temporary SQLite
  database;
- runs `node --test` against it, using the built package as a frontend would.

It covers:
- password and PKCE sign-in, refresh after 401 and before expiry, and
  revocation;
- items with fields, `$filter`/`$orderby`/`$select`/`$count` and paging;
- merge patches with null, 412 on a stale ETag, and validation problems;
- multipart uploads (Blob and byte views), file replacement, downloads and
  page images;
- live processing events, search, and preferences.

**5. Browser deployment.**
- **Recommended:** serve the UI from the same origin as the API, or use a
  dev-server proxy.
- **Other origins:** `Cors:Origins` allows them (off by default), with
  credentials and the `ETag`, `Location`, `Content-Disposition` and
  `Retry-After` headers exposed.
- **Tenancy:** browser navigations (`/connect/authorize`) cannot send
  `X-Tenant`, so multi-tenant browser sign-in selects the tenant by host name.

## Consequences

- **One source of truth:** a new endpoint reaches every SDK by regenerating.
  The contract test catches shapes that would generate badly.
- **Small hand-written layer:** about 600 lines of TypeScript, and none of it
  is protocol code: OAuth and SSE come from libraries.
- **Test time:** the end-to-end tests need the .NET SDK and take about 30
  seconds, so they run on demand and in CI rather than in the .NET suite.
- **Known limits:**
  - fetch cannot report upload progress.
  - Kiota renames some properties (`error` becomes `errorEscaped`).
  - C# and Python get the better OpenAPI description but not the runtime
    helpers. They reuse the same patterns: ETag in the body, `ApiProblem`,
    `@odata.nextLink`.
