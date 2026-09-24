# 0005: Out-of-the-box multitenancy support

- **Status:** mapped
- **Area:** Platform / Security
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): PLT-03…06, PLT-13, EXT-03, IAM-04

## The idea

Multitenancy should be built in from the start, not added later. One
installation can host many isolated tenants (organizations), each with its own
users, workspaces, data, files, settings and installed extensions.

## Why / problem it solves

- Enables a hosted SaaS offering and also shared self-hosted setups
  (e.g. an MSP serving several customers).
- Retrofitting tenant isolation later is expensive and risky.

## Examples / references

- Microsoft 365 / SharePoint Online: tenant, then sites.
- Papermerge has only a storage `prefix` setting for multi-tenant deployments.
- Relates to open decision 2 in [architecture-vision.md](../docs/architecture-vision.md)
  ("design for multi-tenant, ship self-hosted first").

## Notes

<!-- Open questions to settle during review:
     - Isolation model: shared DB with a tenant_id on every row (with EF Core
       global filters and PostgreSQL row-level security), schema per tenant,
       database per tenant, or a mix (allow a premium tenant to get its own DB)?
     - Tenant resolution: subdomain, custom domain, header, or token claim.
     - File storage isolation: prefix or bucket per tenant; per-tenant
       encryption keys?
     - Extensions: installed per tenant; tenant-scoped config and data; how
       in-process extensions are prevented from crossing tenants.
     - Per-tenant auth: own OIDC/SSO provider per tenant.
     - Quotas and limits per tenant (storage, users, OCR jobs), usage metering.
     - Tenant lifecycle: provisioning, suspension, export, deletion.
     - Single-tenant self-hosted = one default tenant, same code path. -->
