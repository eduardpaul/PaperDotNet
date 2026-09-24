# ADR-0002: Staged authentication

**Status:** Accepted (2026-09-24)

## Context
The target is a built-in OAuth2/OIDC server (OpenIddict) for SDKs, MCP clients
and external IdPs. Doing all of that in phase 0 would delay the skeleton.

## Decision
- **P0:** ASP.NET Core Identity local accounts; `POST /v1.0/auth/token`
  (user name + password) issues short-lived JWT access tokens (HMAC, key from
  `Auth:SigningKey`). Personal API tokens (`pdn_…`, SHA-256 hashed, scoped,
  expiring, revocable). A policy scheme picks JWT or API token.
- Authorization is independent of how the caller authenticated: scope
  policies check the user's *current* effective scopes (cached briefly), and
  API tokens are further limited to their own scopes.
- **P1:** OpenIddict (authorization code + PKCE, client credentials, refresh
  tokens, device code), passkeys, MFA, external OIDC. The password endpoint is
  then removed or restricted.

## Consequences
- Endpoints and authorization stay unchanged when OpenIddict arrives; only the
  token issuer changes.
- Tokens carry only identity (`sub`, `tenant_id`, `tenant`); role changes take
  effect within the cache window (1 minute) without re-login.
