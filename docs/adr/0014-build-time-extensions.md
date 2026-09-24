# ADR-0014: Build-time extensions (no runtime plugin loading)

**Status:** Accepted (2026-09-24). Changes the loading model in the technical approach §12.

## Context
Phase 2 adds the extension runtime (EXT-01…05, EXT-07). The original plan
loaded extensions at runtime in collectible `AssemblyLoadContext`s. Runtime
assembly loading is not supported by Native AOT, and trimmed hosts can break
plugins. The owner wants to keep AOT and other modern build types possible.

## Decision
- Extensions are **compiled into the host**: an operator adds them as NuGet
  or project references to a host build (e.g. a custom image). There is no
  drop-in folder and no runtime assembly loading.
- An extension assembly declares `[assembly: PaperDotNetExtension(typeof(X))]`
  and embeds a language-neutral manifest (`extension.json`). A **source
  generator** in the host finds referenced extensions at compile time and
  generates their registration: no assembly scanning or reflection-based
  loading at runtime.
- Only the **operator** installs extension code. **Tenant admins** enable,
  configure and disable installed extensions for their organization; every
  contribution (endpoints, handlers, jobs, field types) is gated per tenant.
- Extension data: list-based storage and, optionally, an own EF schema with
  migrations for both providers, run by the host under the tenant rules.
- Node.js/Python sidecars and remote webhooks (P7) stay the way to add code
  without rebuilding, and work with any build type.

## Consequences
- Adding or upgrading an extension means rebuilding and redeploying the host
  (restart), like any dependency update. Tenants never run code they could
  not have received from the operator.
- One shared contract (`IExtension.Configure(IExtensionBuilder)`) serves all
  build types; the host itself is not AOT-ready yet (Wolverine runtime code
  generation, EF Core, OData, Identity/OpenIddict reflection).
