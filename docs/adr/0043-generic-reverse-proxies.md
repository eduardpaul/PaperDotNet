# ADR-0043: Running behind general-purpose reverse proxies (Nginx Proxy Manager)

- **Status:** Proposed
- **Date:** 2026-10-02

## Context

Many self-hosters put every service behind
[Nginx Proxy Manager](https://nginxproxymanager.com/) (NPM). Some sign
people in with NPM's access lists (HTTP basic auth); others add Authelia or
Authentik through `auth_request`. [ADR-0031](0031-reverse-proxy-sign-in.md)
assumed an authenticating proxy: it vouches for the user, PaperDotNet trusts
its address, and only `/connect/authorize` reads the headers.

Building [samples/nginx-proxy-manager](../../samples/nginx-proxy-manager/README.md)
and testing it against PaperDotNet showed where that assumption breaks. The
tests used nginx with NPM's `proxy.conf`, its http settings and Authelia 4.39.

1. **The proxy's address proves nothing about the path.** A general-purpose
   proxy forwards anonymous traffic from the same address. A natural NPM
   config protects only `location = /connect/authorize`. Its default location
   passes a client's own `Remote-User` header through. Because PaperDotNet's
   routes ignore case, `/CONNECT/AUTHORIZE` with `Remote-User: admin` signed
   in as the administrator without a password. The sample prevents this in
   nginx: a case-insensitive location for the sign-in, and the headers
   cleared everywhere else. That works, but it fails open: one missed
   location, or a later route alias, and anyone can choose their user.
2. **Basic auth leaks into OAuth.** Browsers send basic-auth credentials
   again to every path below the one that asked for them (RFC 7617
   protection space). Signing in at `/connect/authorize` therefore puts
   `Authorization: Basic` on the web UI's request to `/connect/token`.
   OpenIddict reads that as client credentials and rejects the request
   (`invalid_client`). The sample removes the header from browser requests
   with a map keyed on `Sec-Fetch-Site`. That is a workaround in the proxy
   for a path choice in PaperDotNet.
3. **Forwarded headers are trusted from anyone.** The host clears
   `KnownProxies` and `KnownIPNetworks`. With `ForwardedHeaders:Enabled`,
   any client that reaches the port directly can set the scheme, the host
   (and with it host-based tenant resolution) and its own address. NPM
   itself passes on a client's `X-Forwarded-Proto` when it is `http` or
   `https`.
4. **Streams stall.** nginx buffers responses by default, and NPM's read
   timeout is 90 s. Server-sent events (`/v1.0/me/events`, MCP responses) send
   no `X-Accel-Buffering: no` and no keep-alive. Behind NPM, no live event
   arrived within 3 s, and quiet streams are cut. The sample adds a location
   with buffering off.
5. **The proxy cannot take away what it gave.** Users are added to groups
   the proxy names, but never removed. A user removed at the proxy keeps
   their refresh token (14 days), because the proxy is asked only at
   interactive sign-in.
6. **Signing out does not sign out.** After PaperDotNet's end-session, the
   next visit to `/connect/authorize` goes through the proxy again, and the
   proxy still has a session. The user is signed straight back in.

## Decision

1. **A shared secret proves that the proxy authenticated the request.**
   - New settings: `Auth:ReverseProxy:Secret` and `SecretHeader` (default
     `X-PaperDotNet-Proxy`).
   - When a secret is set, the identity headers count only if this header
     matches it (constant-time comparison), in addition to the trusted peer
     address.
   - The proxy adds the header only in the location where it authenticated
     the user. A request that reaches the sign-in path any other way has no
     secret and is ignored, so mistakes fail closed.
   - When the proxy is enabled without a secret, a startup warning names
     this risk. The secret may become required in a later version.
2. **A sign-in path of its own, outside `/connect/`.**
   - `GET /auth/proxy/sign-in?returnUrl=…` reads the proxy's headers,
     starts the sign-in session (as `/connect/authorize` does today) and
     redirects to `returnUrl`. Only the local `/connect/authorize…` is
     accepted there, so there are no open redirects.
   - When the proxy is enabled, `/connect/authorize` sends signed-out users
     to this path instead of `/login`.
   - The proxy protects only `/auth/proxy/`, so basic-auth credentials are
     never sent to the OAuth endpoints. In NPM that is one custom location
     with an access list, or an `auth_request` there. No server-wide
     Advanced config is needed.
   - Login CSRF is harmless: the endpoint can only sign the visitor in as
     whoever the proxy says they are.
   - `/connect/authorize` keeps reading the headers, so proxies that
     authenticate every path (ADR-0031) keep working. Both paths check the
     peer address and the secret.
3. **Forwarded headers only from known proxies.**
   - New settings: `ForwardedHeaders:KnownProxies` (addresses or CIDR,
     validated at startup, like `TrustedProxies`) and
     `ForwardedHeaders:ForwardLimit` (default 1; 2 for Cloudflare → NPM).
   - Without `KnownProxies`, `Auth:ReverseProxy:TrustedProxies` is used.
     Without either, any peer is trusted as today, with a startup warning.
4. **Streams that pass through buffering proxies.**
   - Server-sent event responses (live events and MCP's streamed
     responses) send `X-Accel-Buffering: no`.
   - Live events send a comment line every 30 s, below NPM's 90 s and
     Cloudflare's 100 s timeouts.
   - Proxies then need no special location for streams.
5. **Group sync, opt-in.**
   - `Auth:ReverseProxy:GroupSync` is either `Add` (default, today's
     behavior) or `Sync`.
   - Groups get a `Source` (`local` or `proxy`); this needs a migration for
     both providers. `CreateGroups` (default off) creates groups the proxy
     names, with `Source = proxy`.
   - With `Sync`, each proxy sign-in adds the user to the named groups and
     removes them from proxy-sourced groups the proxy did not name. Local
     groups are never touched.
   - Changes go through the same membership code as the admin endpoints,
     with the same cache invalidation.
   - The last-administrator rule applies. A sync that would remove the last
     administrator keeps that membership and logs a warning.
6. **Proxy sign-ins are re-checked daily.**
   - `Auth:ReverseProxy:RefreshTokenLifetime` (default 1 day) applies to
     grants that began with a proxy sign-in. It is set on the principal, as
     OpenIddict allows per token.
   - When it ends, the web UI goes through `/connect/authorize` again. This
     re-reads the user, the groups and whether the proxy still admits them.
     With a live proxy session, that is a redirect without a prompt.
   - API tokens are not affected. Disabling the user in PaperDotNet still
     ends all access at once.
7. **Signing out goes to the proxy.**
   - `Auth:ReverseProxy:LogoutUrl` (for example
     `https://auth.example.com/logout`) is where the end-session endpoint
     sends the browser after ending PaperDotNet's session.
   - The web UI's signed-out page does not start a new sign-in by itself.
   - Basic auth has no logout. The sample documents that.

## Not decided here

- **OAuth for MCP clients that cannot send a static header.** This needs
  protected resource metadata (RFC 9728) and a `WWW-Authenticate`
  `resource_metadata` hint on `/v1.0/mcp`. It also needs client
  registration: dynamic registration or client ID metadata documents. It is
  its own ADR. With 2 in place, the browser part of that flow already goes
  through the proxy. Until then, MCP clients use API tokens, which work
  through any proxy.
- **Accepting tokens issued by the identity provider (JWTs from
  Authelia/Authentik) at the API.** This is external OIDC login (IAM-04).
  Behind a proxy, PaperDotNet keeps issuing its own tokens.
- **Provisioning pushed from the identity provider (SCIM).** Point 6 limits
  how long a removed user keeps working; it does not remove them.

## Consequences

- NPM setups get simpler and fail closed:
  - one protected custom location (`/auth/proxy/`) that sets the user
    headers and the secret;
  - no `http_top.conf` maps;
  - no stream location;
  - no need to clear the `Remote-*` headers in every location.

  The sample is updated when this is built. Until then, its config is the
  supported way.
- The Identity module gets new options, a `Group.Source` column (migrations
  for SQLite and PostgreSQL), and one anonymous endpoint, which needs a
  tenant-isolation test.
- Tests are needed for:
  - a missing or wrong secret;
  - spellings of both sign-in paths;
  - forwarded headers from unknown peers;
  - the `X-Accel-Buffering` header and the keep-alive;
  - sync adding and removing members, including the last administrator;
  - the shorter refresh token after a proxy sign-in.
- Existing ADR-0031 setups keep working unchanged. Until they add a secret,
  they see a startup warning.
