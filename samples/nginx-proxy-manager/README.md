# PaperDotNet behind Nginx Proxy Manager

A Docker Compose setup that runs PaperDotNet behind
[Nginx Proxy Manager](https://nginxproxymanager.com/) (NPM):
- NPM handles TLS (Let's Encrypt) and signs people in.
- PaperDotNet takes the user, and optionally their groups, from the proxy
  (IAM-15, [ADR-0031](../../docs/adr/0031-reverse-proxy-sign-in.md)).
- Tokens, MCP, groups and live events work as without a proxy.

There are two ways to sign in. Pick one:

| | **A. NPM access list** | **B. Authelia** (profile `authelia`) |
|---|---|---|
| Extra containers | none | Authelia |
| Users are kept in | NPM (Access Lists) | Authelia (`authelia/users_database.yml`, or LDAP) |
| Name and e-mail | no (user name only) | yes |
| Groups | a map in `npm/http_top.conf` | Authelia's groups |
| Two-factor, passkeys | no | yes |
| Single sign-on with other apps | no | yes |
| Signing out | the browser keeps the password until it closes | Authelia's logout |

The design changes that would make this simpler and safer are in
[ADR-0043](../../docs/adr/0043-generic-reverse-proxies.md).

## What works through the proxy

| Feature | How |
|---|---|
| Web UI | Opening it signs you in through the proxy. There is no PaperDotNet password page. |
| API, SDKs, `pdn` CLI | `Authorization: Bearer` with an OAuth access token from `/connect/token`, or an API token (`pdn_…`). |
| MCP (`/v1.0/mcp`) | An API token with `mcp.use` in the client's headers (see [MCP clients](#mcp-clients)). |
| Groups | The proxy names groups at sign-in; users join existing PaperDotNet groups of those names. Roles given to a group apply to its members. |
| Live events (`/v1.0/me/events`) | Server-sent events, with buffering off for that path. |
| Uploads | Up to 2000 MB (NPM's limit), within PaperDotNet's own upload limits. |
| Passkeys, OIDC discovery | Use the public host name, since NPM forwards the scheme and keeps the `Host` header. |

About "JWT": PaperDotNet issues its own tokens. Its access tokens are
encrypted (opaque), and only the ID token is a JWT. Clients send them back as
they are and do not read them. PaperDotNet does not accept tokens issued by
NPM, Authelia or another identity provider. That is external OIDC login
(IAM-04, planned).

## How it works

```
browser ──https──▶ NPM ──http──▶ paperdotnet:8080
                    │
                    ├─ /connect/authorize ─▶ access list or Authelia ─▶ adds Remote-User / -Groups / -Name / -Email
                    ├─ /v1.0/me/events, /v1.0/mcp ─▶ no buffering, 1 h read timeout
                    └─ everything else ─▶ Remote-* headers removed, tokens only
```

- **Sign-in path only.** NPM authenticates only the sign-in path
  (`/connect/authorize`). There it tells PaperDotNet who the user is, and
  PaperDotNet issues its usual tokens. The API never reads the proxy's
  headers, so cross-site requests that carry the proxy's cookie cannot act
  as the user.
- **NPM's address only.** PaperDotNet trusts these headers only from NPM's
  address on the shared network (`NPM_IP`, `Auth:ReverseProxy:TrustedProxies`).
  The PaperDotNet container publishes no port, so NPM is the only way in.
- **The pasted config replaces NPM's default location.** It defines the
  locations itself, so NPM leaves out its own `location /`. In the other
  locations, the `Remote-*` headers are set to empty values. Without that,
  anyone could send `Remote-User: admin` to `/CONNECT/AUTHORIZE`. That path
  is spelled differently from `location = /connect/authorize`, but
  PaperDotNet still treats it as the sign-in endpoint. A test showed that it
  then signs in as the administrator without a password. The sample's
  sign-in location is case-insensitive and allows a trailing slash.

## Pitfalls this sample avoids

1. **No access list on the whole proxy host.** With basic auth on `location /`:
   - NPM removes the `Authorization` header (unless "Pass Auth to Host" is
     on), so no token reaches PaperDotNet.
   - Clients would have to send basic auth and a token in the same header.

   The web UI, the API and MCP all break.
2. **Basic auth re-sent to `/connect/token`.** After signing in at
   `/connect/authorize`, browsers send the access list's password again to
   every path below `/connect/`. OpenIddict reads it as client credentials
   and rejects the web UI's token request (`invalid_client`). The map in
   `npm/http_top.conf` removes it from browser requests. Browsers send
   `Sec-Fetch-Site`; server-side OAuth clients don't, so they keep their own
   Basic credentials.
3. **Live events need buffering off.** NPM buffers responses, so server-sent
   events only arrive when the buffer fills. Its 90 s read timeout also ends
   quiet streams.
4. **Turn off "Block Common Exploits".** It answers 403 to query strings
   that contain, for example:
   - `concat(`, which OData filters can contain;
   - `union … select (`, which search terms can contain;
   - `=http://`, as in an unencoded loopback `redirect_uri`.
5. **Leave "Cache Assets" off.** PaperDotNet sets its own cache headers
   (hashed files are cached for a year, everything else is revalidated).

## Setup

### 1. Configure and start

```bash
cd samples/nginx-proxy-manager
cp .env.example .env    # host names, admin password, NPM address; for Authelia: AUTH_HOST, COOKIE_DOMAIN, secrets
docker compose up -d    # builds the PaperDotNet image from this repository on the first run
```

`COMPOSE_PROFILES` in `.env` selects what starts:
- `npm`: NPM and PaperDotNet.
- `npm,authelia`: the same plus Authelia.
- empty: only PaperDotNet, to use with [your existing NPM](#using-the-npm-you-already-run).

The DNS names (`PAPERDOTNET_HOST`, and `AUTH_HOST` for Authelia) must point
to this server, and ports 80 and 443 must be reachable for Let's Encrypt.

### 2. Create the proxy host in NPM

Open NPM at `http://<server>:81`. The first start asks you to create an NPM
admin account. Then go to **Hosts → Proxy Hosts → Add Proxy Host**:

| Tab | Setting |
|---|---|
| Details | Domain names: `PAPERDOTNET_HOST`. Scheme `http`, forward hostname `paperdotnet`, port `8080`. **Cache Assets: off. Block Common Exploits: off.** Websockets Support: either (not used). Access List: **Publicly Accessible** |
| SSL | Request a new certificate. **Force SSL: on.** HTTP/2: on. HSTS: as you like |
| Advanced | Paste one of the files below |

**A. NPM access list:**
1. Under **Access Lists → Add Access List**, add the users under
   *Authorization*. Leave "Pass Auth to Host" off and add no *Access* rules.
2. Find the list's id: `docker compose exec npm ls /data/access`, or the
   order in which you created the lists.
3. Paste [`npm/advanced-access-list.conf`](npm/advanced-access-list.conf)
   into **Advanced** and replace `ACCESS_LIST_ID` with that id.
4. Groups: edit the map in [`npm/http_top.conf`](npm/http_top.conf), then
   restart NPM (`docker compose restart npm`).

**B. Authelia:**
1. Add a second proxy host for `AUTH_HOST`: scheme `http`, host `authelia`,
   port `9091`, with SSL and Force SSL. It needs no Advanced config.
2. Paste [`npm/advanced-authelia.conf`](npm/advanced-authelia.conf) into the
   **Advanced** tab of the PaperDotNet proxy host.
3. Users and groups live in
   [`authelia/users_database.yml`](authelia/users_database.yml). Both demo
   users have the password `change-me-demo`.
4. Two-factor sign-in: set `policy: two_factor` in
   [`authelia/configuration.yml`](authelia/configuration.yml). Registration
   e-mails are written to a file: `docker compose exec authelia cat /var/lib/authelia/notification.txt`.

### 3. First sign-in, groups and roles

1. Open `https://PAPERDOTNET_HOST`. The proxy asks you to sign in.
   - The proxy user **`admin`** is PaperDotNet's first administrator. Users
     with the same name are the same account.
   - Every other user gets an account as a Member on first sign-in
     (`CREATE_USERS=true`).
2. As `admin`, create the groups the proxy names (Admin → Groups), for
   example **Admins** and **Finance**. PaperDotNet never creates groups
   itself.
3. Give roles to groups instead of people. For example, assign the
   *Administrator* role to the group *Admins*.
4. Users join their groups at their next sign-in through the proxy.
   - Users are **never removed** from groups by the proxy. Remove them in
     PaperDotNet.
   - A user removed from the proxy keeps their refresh token (14 days) and
     API tokens. **Disable** them in PaperDotNet to end access at once.
   - [ADR-0043](../../docs/adr/0043-generic-reverse-proxies.md) proposes
     group sync and shorter sessions for proxy sign-ins.

### 4. MCP clients

Create an API token in the web UI (**Settings → API tokens**) with the
scopes the assistant may use. For example, `mcp.use search.read list.read
document.read` gives read-only access. Then add it to the client:

```json
{
  "mcpServers": {
    "paperdotnet": {
      "type": "http",
      "url": "https://docs.example.com/v1.0/mcp",
      "headers": { "Authorization": "Bearer pdn_…" }
    }
  }
}
```

This works with clients that accept headers, such as Claude Code, VS Code
and Cursor. Some connectors only offer the OAuth sign-in from the MCP spec,
with discovery and client registration. PaperDotNet does not offer that yet;
see ADR-0043, "Not decided here".

### 5. Scripts, SDKs and the CLI

Users created by the proxy have no password, so they use API tokens:

```bash
export PAPERDOTNET_URL=https://docs.example.com PAPERDOTNET_TOKEN=pdn_…
pdn workspaces
```

Server-side apps can use client credentials at `/connect/token`
(Admin → Applications). The local `admin` can still use the password grant.
To allow only proxy sign-ins, set `PAPERDOTNET__Auth__AllowPasswordGrant=false`.

## Using the NPM you already run

1. In `.env`, set `COMPOSE_PROFILES=` (empty), or `authelia`, then run
   `docker compose up -d`. This creates the network `paperdotnet-proxy`.
2. Attach your NPM to that network with the fixed address `NPM_IP`, in your
   NPM's compose file:

   ```yaml
   services:
     npm:                          # your NPM service
       networks:
         default:
         paperdotnet-proxy:
           ipv4_address: 172.30.10.2   # = NPM_IP
   networks:
     paperdotnet-proxy:
       external: true
   ```

   For a quick test without editing it:
   `docker network connect --ip 172.30.10.2 paperdotnet-proxy <npm-container>`.
   This does not survive recreating the container.
3. Access-list mode only: copy [`npm/http_top.conf`](npm/http_top.conf) to
   `/data/nginx/custom/http_top.conf` in NPM's data folder, or append it to
   the file if you already have one. Then restart NPM.
4. Continue with [step 2](#2-create-the-proxy-host-in-npm).

Do not trust the whole subnet (`172.30.10.0/24`) unless only NPM and
PaperDotNet are on that network. Any container on a trusted network could
name a user.

## Checking the setup

```bash
H=docs.example.com
curl -sI "https://$H/connect/authorize" | head -n 1          # 401 (access list) or 302 to Authelia
curl -s -o /dev/null -w '%{http_code}\n' -H 'Remote-User: admin' \
  "https://$H/CONNECT/AUTHORIZE?client_id=paperdotnet"        # 401/302 as well: the header does not get through
curl -s "https://$H/.well-known/openid-configuration" | grep -o '"issuer":"[^"]*"'   # https://docs.example.com/
curl -sN -H "Authorization: Bearer pdn_…" "https://$H/v1.0/me/events" | head -n 2   # "event: connected" at once
```

**How this sample was tested:**
- The NPM config was run in nginx with NPM's own `proxy.conf`, http
  settings and forced-SSL block, in front of PaperDotNet, with Authelia
  4.39. The test did not use the NPM container.
- What passed:
  - sign-in with the access list and with Authelia;
  - name, e-mail and groups taken over;
  - the token exchange, including a browser that re-sends basic auth;
  - the API;
  - live events and MCP (`initialize`, `tools/list`);
  - the issuer in OIDC discovery;
  - forged `Remote-User` headers on all spellings of the sign-in path were
    rejected.
- `compose.yml` was checked with `docker compose config` for every profile.

## Files

| File | What it is |
|---|---|
| `compose.yml` | NPM (profile `npm`), PaperDotNet, Authelia (profile `authelia`) on the network `paperdotnet-proxy` |
| `.env.example` | Host names, secrets, NPM address, profiles |
| `npm/advanced-access-list.conf` | Advanced config for mode A |
| `npm/advanced-authelia.conf` | Advanced config for mode B |
| `npm/http_top.conf` | Http-level maps for mode A: groups per user, and removing basic auth from browser requests to `/connect/` |
| `authelia/configuration.yml` | Authelia: file users, one access rule, session cookie for `COOKIE_DOMAIN` |
| `authelia/users_database.yml` | Demo users `admin` (Admins) and `alice` (Finance) |
