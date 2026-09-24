# ADR-0006: Phase 0 simplifications and deferrals

**Status:** Accepted (2026-09-24)

Practical choices made while building the P0 skeleton, compared with
[technical-approach.md](../technical-approach.md):

| Topic | P0 | Later |
|---|---|---|
| IDs | `Guid` UUIDv7 via `Ids.New()` | Strongly-typed ID records when the Lists engine lands (P1) |
| Architecture tests | Reflection on assembly references + source scans (no extra dependency) | ArchUnitNET if type-level rules are needed |
| Request validation | DataAnnotations via `RequestValidation.Validate` in handlers (the .NET 10 validation source generator does not see endpoints in module assemblies) | Revisit when the generator supports it |
| Identity schema | Identity store schema v1/v2 (no passkeys) | Passkeys with OpenIddict in P1 |
| Row-level security | Not yet | P1 (ADR-0003) |
| Data Protection keys | Default file store | Stored in PostgreSQL in P1 |
| Aspire AppHost | Not yet; `ServiceDefaults` project exists | Add when a second process (worker) appears |
