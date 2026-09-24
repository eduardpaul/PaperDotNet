# ADR-0003: Tenant resolution with Finbuckle, isolation in EF Core

**Status:** Accepted (2026-09-24)

## Decision
- **Resolution** uses Finbuckle.MultiTenant with a read-only store over the
  `tenancy.tenants` table. Order: custom host mapping → host template (opt-in)
  → `X-Tenant` header (opt-in, dev/tests) → token claim → default tenant.
  The default tenant applies only when no tenant was requested explicitly.
- **Guard:** API requests without an active tenant get `404 tenantNotFound`;
  credentials used against another tenant get `403 tenantMismatch`.
- **Isolation** is ours, not Finbuckle's: every `ITenantOwned` entity gets the
  named EF Core 10 query filter `Tenant`; a `SaveChanges` interceptor stamps
  `TenantId` and rejects cross-tenant writes. Architecture tests forbid
  disabling the `Tenant` filter.
- Background work, CLI and bootstrap run inside `ITenantScopeFactory` scopes.
- API tokens and tenants are platform-level lookup tables (queried before the
  tenant is known) and carry their tenant explicitly.

## Deferred
PostgreSQL row-level security as defense in depth moves to P1, together with
the outbox (both need connection-level tenant context).

## Alternatives considered (reviewed 2026-09-24)

- **EF Core built-in multitenancy:** none exists, up to and including EF Core 11.
  EF provides building blocks, not tenant resolution. The Microsoft
  [multi-tenancy guidance](https://learn.microsoft.com/en-us/ef/core/miscellaneous/multitenancy)
  lists three approaches:
  - **Tenant column** ("discriminator"): supported through global query
    filters. This is our approach, using EF 10 named filters.
  - **Database per tenant:** supported through configuration (a different
    connection string per tenant).
  - **Schema per tenant:** "not supported".

  The app must supply the tenant, e.g. via its own `ITenantService`.
- **ASP.NET Core** has no tenant resolution either, so Finbuckle fills that gap.
- If PaperDotNet later needs database-per-tenant for large customers:
  - Put the connection string in the tenant record and pick it in the
    `IDatabaseProvider`.
  - Do not use DbContext pooling with tenant-dependent options.
