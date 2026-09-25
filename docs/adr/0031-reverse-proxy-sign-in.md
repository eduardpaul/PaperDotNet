# ADR-0031: Sign-in through an authenticating reverse proxy

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Self-hosters often put services behind Authelia, Authentik or oauth2-proxy
(IAM-15). The proxy authenticates the user and passes the name in headers
such as `Remote-User`.

Trusting such headers is dangerous in two ways:
- **Spoofing:** anyone who reaches the server directly can send the headers.
- **Cross-site requests:** the proxy adds them to every request that carries
  its cookie, including cross-site ones (CSRF).

## Decision

- **Off by default.** It is turned on with `Auth:ReverseProxy:Enabled`, and
  then requires `TrustedProxies` (addresses or CIDR networks, checked at
  startup).
- **Only the direct peer is trusted.** The host captures the connection's
  address before forwarded headers are applied (`PeerAddress`), so an
  `X-Forwarded-For` value can never make a client look like the proxy.
- **Only `/connect/authorize` reads the headers.** There, the proxy's user
  starts a sign-in session, and the OAuth flow (state, PKCE, registered
  redirect URIs) issues tokens as usual. The API keeps using tokens, so
  cross-site requests cannot act on the proxy's authentication.
- **Users:**
  - Unknown users are created without a password and with the Member role
    (`CreateUsers`, on by default).
  - Name and e-mail are updated from `Remote-Name` and `Remote-Email`.
  - Users are added to existing groups named in `Remote-Groups`, never
    removed from groups.
  - Disabled and deleted users cannot sign in.
  - The header names can be configured.

## Consequences

- The web UI signs in behind a proxy without a login page. Scripts keep using
  API tokens.
- There is no header login for the API directly. Clients that only have the
  proxy's cookie need an OAuth flow.
- Users created by the proxy can get a password later (admin reset) if they
  also need to sign in without it.
