# 0021: Portable configuration templates (export/import as XML)

- **Status:** mapped
- **Area:** Lists/Platform
- **Date:** 2026-09-24
- **Mapped to:** PRV-01…05 (P5; content in templates P7), related LST-16, PLT-13

## The idea

Export lists and other configuration (content types, fields, views, list
templates, term sets, permissions, extension settings, …) to an XML file, and
apply that file to another workspace, tenant or installation. Inspired by the
PnP provisioning engine (PnP Provisioning Schema / `Get-PnPSiteTemplate` and
`Invoke-PnPSiteTemplate`).

## Why / problem it solves

- Move a setup from test to production, or between tenants and installations.
- Share ready-made solutions (e.g. "invoice approval workspace") as a file.
- Keep configuration in source control and review changes as diffs.

## Examples / references

- PnP Provisioning Engine and PnP Provisioning Schema (XML) for SharePoint.
- `Get-PnPSiteTemplate` (extract) / `Invoke-PnPSiteTemplate` (apply).

## Notes

- Configuration only by default; list content (items, documents) optional.
- A published XML schema (XSD) so files can be validated and edited with tooling.
- Apply should be idempotent (create or update), with a dry run that shows the changes.
- Extensions could add their own sections (like PnP extensibility handlers).
- Related: list templates (LST-16) and extension content types.
