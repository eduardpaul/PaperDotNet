# ADR-0043: Running behind general-purpose reverse proxies (Nginx Proxy Manager)

- **Status:** Accepted (implemented)
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
   - New settings: `Auth:ReverseProxy:Secret` (at least 16 characters) and
     `SecretHeader` (default `X-PaperDotNet-Proxy`).
   - When a secret is set, the identity headers count only if this header
     matches it, in addition to the trusted peer address. The comparison
     takes constant time.
   - The proxy adds the header only in the location where it authenticated
     the user. A request that reaches a sign-in path any other way has no
     secret and is ignored, so mistakes fail closed.
   - When the proxy is enabled without a secret, a startup warning names
     this risk.
2. **A sign-in path of its own, outside `/connect/`.**
   - `GET /auth/proxy/sign-in?returnUrl=…` reads the proxy's headers,
     starts the sign-in session and redirects to `returnUrl`. Only this
     server's `/connect/authorize…` is accepted there, otherwise it
     redirects to `/`, so there are no open redirects.
   - If the proxy names nobody, the path sends the browser to the password
     sign-in (`Auth:LoginUrl`, the web UI's `/login`), or answers
     `401 proxySignInFailed` when there is none.
   - When the proxy is enabled, `/connect/authorize` sends signed-out users
     to this path instead of `/login`.
   - The proxy protects only `/auth/proxy/`, so basic-auth credentials are
     never sent to the OAuth endpoints. In NPM that is one location with an
     access list or an `auth_request`.
   - Login CSRF is harmless: the endpoint can only sign the visitor in as
     whoever the proxy says they are.
   - `/connect/authorize` keeps reading the headers, so proxies that
     authenticate every path (ADR-0031) keep working. Both paths check the
     peer address and the secret.
3. **Forwarded headers only from known proxies.**
   - New settings: `ForwardedHeaders:KnownProxies` (addresses or CIDR) and
     `ForwardedHeaders:ForwardLimit` (default 1; 2 for Cloudflare → NPM).
   - Without `KnownProxies`, `Auth:ReverseProxy:TrustedProxies` is used.
     Without either, any peer is trusted as before, with a startup warning.
   - An entry that is neither an address nor a network stops the startup.
4. **Streams that pass through buffering proxies.**
   - Responses of type `text/event-stream` to requests that accept event
     streams send `X-Accel-Buffering: no`. This covers live events and MCP's
     streamed responses.
   - A quiet live event stream sends a `keepalive` event every 30 s
     (`Jobs:LiveEventsKeepAlive`), below NPM's 90 s and Cloudflare's 100 s
     timeouts. It is an event, not a comment, because `SseItem` cannot
     write comments. `EventSource` clients ignore event types they do not
     listen to.
   - Proxies then need no special location for streams.
5. **Group sync, opt-in.**
   - `Auth:ReverseProxy:GroupSync` is either `Add` (default, the behavior
     before this ADR) or `Sync`.
   - Groups have a `Source`, `Local` or `Proxy`; the migration covers both
     providers. With `CreateGroups` (default off), groups the proxy names
     that do not exist yet are created with `Source = Proxy`.
   - With `Sync`, each proxy sign-in adds the user to the named groups and
     removes them from proxy groups the proxy no longer names. An empty or
     missing groups header names none. Local groups only ever gain members.
   - The groups header may now be up to 8 KB, up to 100 groups. Before, it
     was ignored above 256 characters.
   - Membership changes invalidate the same caches as the admin endpoints.
   - The last-administrator rule applies. A sync that would remove the last
     administrator keeps that membership and logs a warning.
   - The source is not part of the API yet. Groups created by
     administrators stay local even if the proxy names them.
6. **Proxy sign-ins are re-checked daily.**
   - `Auth:ReverseProxy:RefreshTokenLifetime` (default 1 day, at least 5
     minutes) limits sign-ins the proxy started.
   - The sign-in time travels in the sign-in session, the authorization
     code and the refresh tokens. It is never put in access or identity
     tokens.
   - Each refresh issues tokens that end when that time is up, never later,
     however often the user refreshes. The sign-in session does not
     continue it either.
   - Afterwards, the web UI goes through the proxy again. This re-reads the
     user, the groups and whether the proxy still admits them. With a live
     proxy session, that is a redirect without a prompt.
   - API tokens are not affected. Disabling the user in PaperDotNet still
     ends all access at once.
7. **Signing out goes to the proxy.**
   - `Auth:ReverseProxy:LogoutUrl` (for example
     `https://auth.example.com/logout`) is where `/connect/logout` sends the
     browser after ending a session the proxy started. Other sessions keep
     the client's post-logout redirect.
   - Basic auth has no logout. The sample documents that.

8. **Local sign-in can be turned off.**
   - The security review for exposing the sample on the internet found a
     bypass. Even behind Authelia with two-factor, the first administrator's
     local password (`Bootstrap:AdminPassword`) still worked at
     `/v1.0/auth/login` and through the password grant. So did passkeys
     users registered in PaperDotNet, even after the proxy removed them.
     This skipped the proxy's second factor, its bans and deprovisioning.
   - `Auth:LocalSignIn` (default true) turns this off. Password sign-in,
     passkey sign-in and the password grant are then refused
     (`403 localSignInDisabled`). `/connect/authorize` ignores sign-in
     sessions the proxy did not start, and the proxy sign-in path no longer
     falls back to the password page.
   - API tokens and client credentials keep working.
   - It can only be off while the proxy is enabled; otherwise nobody could
     sign in. The NPM sample turns it off. For a break-glass sign-in, turn
     it back on for a moment.

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

- NPM setups are simpler and fail closed. The
  [sample](../../samples/nginx-proxy-manager/README.md) needs one
  `location /auth/proxy/` that authenticates and sets the user headers and
  the secret. It no longer needs:
  - the maps for `Authorization` headers;
  - a location for streams;
  - clearing the `Remote-*` headers everywhere.
- The Identity module has new options, a `Group.Source` column with
  migrations for SQLite and PostgreSQL, and the anonymous `/auth/proxy/sign-in`
  endpoint. Its tenant is the request's, like `/connect/authorize`'s.
- Integration tests (`ReverseProxyTests`, `LocalSignInTests`) cover:
  - a missing or wrong secret, and an untrusted peer;
  - the sign-in path and `returnUrl`;
  - token lifetimes through a refresh;
  - the proxy logout;
  - group sync, including local groups and the last administrator;
  - the no-buffering header and keep-alives;
  - which proxies forwarded headers count from;
  - refusing local sign-in when it is turned off.
- Existing ADR-0031 setups keep working. Two things change:
  - signed-out browsers now go to `/auth/proxy/sign-in` instead of `/login`;
  - they see a startup warning until they add a secret.
