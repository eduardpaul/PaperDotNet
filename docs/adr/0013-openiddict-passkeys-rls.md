# ADR-0013: OpenIddict, passkeys, row-level security and Data Protection in the database

**Status:** Accepted (2026-09-24). Completes stage 2 of [ADR-0002](0002-staged-authentication.md).

## Context
Phase 1f hardens identity: standard OAuth flows for apps, SDKs and MCP clients
(IAM-02), passwordless sign-in (IAM-01), a second tenant wall in the database,
and keys that survive restarts and work on several nodes. The minimal install
must stay one container without secrets to manage.

## Decision
- **OpenIddict** (Apache-2.0) is the OAuth 2.0 / OpenID Connect server
  (`/connect/authorize`, `/token`, `/userinfo`, `/logout`, `/revoke`, discovery):
  - authorization code + PKCE (required), refresh tokens, client credentials,
    and the password grant only for the built-in public client `paperdotnet`
    (CLI, scripts; `Auth:AllowPasswordGrant`). The homemade HMAC JWT and
    `POST /v1.0/auth/token` are removed, and so is `Auth:SigningKey`.
  - Access tokens, codes and refresh tokens use the **Data Protection** format;
    identity tokens are signed with a persisted RSA key (`identity.server_keys`,
    private key protected by Data Protection).
  - Clients (`/v1.0/applications`) belong to a tenant (tenant-filtered
    OpenIddict entities; client ids unique per tenant) and are trusted by it:
    no consent screen. Client credentials act as a **service account** user,
    so roles, groups and workspace membership apply unchanged.
  - Tokens carry identity only; authorization still uses the user's current
    scopes. Tokens without the `api` scope are limited to the permission
    scopes they were granted (like API tokens).
  - `/connect/authorize` uses a sign-in **session cookie** created by
    `POST /v1.0/auth/login` or passkey sign-in; without it, 401 or a redirect
    to `Auth:LoginUrl` (the future UI). APIs never accept the cookie.
- **Passkeys** use ASP.NET Core Identity 10 (`IPasskeyHandler`, schema v3).
  Ceremony state travels through the client, protected by Data Protection and
  bound to tenant and user, so no server-side session is needed.
  `Auth:PasskeyServerDomain`/`PasskeyOrigins` name the UI's domain.
- **Data Protection** keys live in `identity.data_protection_keys` (EF, both
  providers): all nodes share cookies, tokens and protected secrets.
- **PostgreSQL row-level security** (`Database:RowLevelSecurity`, default on):
  every EF connection sets `app.tenant_id`; after migrations every tenant-owned
  table gets `ENABLE`/`FORCE ROW LEVEL SECURITY` and a tenant policy (`USING`
  and `WITH CHECK`), applied from the EF model so new tables are covered
  automatically. Superusers bypass RLS: the app warns at startup, and the
  PostgreSQL test run uses an ordinary role that owns the database.

## Addendum (3d): maintenance sessions

Whole-database backup and restore must see every tenant. The policy condition
is `tenant_id = app.tenant_id OR app.maintenance = 'on'`; only `pg_dump` and
`pg_restore` started by `paperdotnet backup|restore` set `app.maintenance`
(through `PGOPTIONS`). Application code never sets it, so a missing tenant
filter in the app is still caught by RLS.

## Consequences
- No secret needs configuring for authentication; keys are generated and
  stored in the database (protect database backups accordingly).
- Tenant resolution happens before authentication, so OAuth clients select
  the tenant by host or header; the claim strategy only uses API tokens.
- One extra round trip per PostgreSQL connection open (`set_config`).
- Not yet: MFA/TOTP, external identity providers, device code flow, consent
  for third-party apps, sharing with users outside a workspace.
