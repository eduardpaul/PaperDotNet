# PaperDotNet behind Nginx Proxy Manager

A Docker Compose setup that runs PaperDotNet behind
[Nginx Proxy Manager](https://nginxproxymanager.com/) (NPM):
- NPM handles TLS (Let's Encrypt) and signs people in.
- PaperDotNet takes the user and their groups from the proxy (IAM-15,
  [ADR-0031](../../docs/adr/0031-reverse-proxy-sign-in.md),
  [ADR-0043](../../docs/adr/0043-generic-reverse-proxies.md)).
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
| Signing out | the browser keeps the password until it closes | Authelia's logout (`PROXY_LOGOUT_URL`) |

## What works through the proxy

| Feature | How |
|---|---|
| Web UI | Opening it signs you in through the proxy. There is no PaperDotNet password page. |
| API, SDKs, `pdn` CLI | `Authorization: Bearer` with an OAuth access token from `/connect/token`, or an API token (`pdn_…`). |
| MCP (`/v1.0/mcp`) | An API token with `mcp.use` in the client's headers (see [MCP clients](#mcp-clients)). |
| Groups | The proxy names groups at sign-in. PaperDotNet creates missing ones and keeps memberships in sync. Roles given to a group apply to its members. |
| Live events (`/v1.0/me/events`) | Server-sent events. PaperDotNet turns off NPM's buffering for them and sends a keep-alive every 30 s. |
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
                    ├─ /auth/proxy/ ─▶ access list or Authelia ─▶ adds Remote-User / -Groups / -Name / -Email
                    │                                              and the secret (X-PaperDotNet-Proxy)
                    └─ everything else ─▶ passed on as is; tokens only
```

1. A signed-out browser opens the web UI. The UI starts the OAuth sign-in at
   `/connect/authorize`.
2. PaperDotNet sends the browser to `/auth/proxy/sign-in`. That is the only
   path NPM protects.
3. NPM asks for the password, or sends the browser to Authelia. It then
   passes on the request with the user's name, groups and the **proxy
   secret**.
4. PaperDotNet starts a sign-in session and continues the OAuth flow. The
   web UI then gets its usual tokens.

**Why this is safe:**
- **The user headers need two things.** They count only from NPM's address
  (`NPM_IP`) and only with the secret (`PROXY_SECRET`). NPM sends the secret
  only after authenticating, in that one location. If someone sends
  `Remote-User: admin` any other way, PaperDotNet ignores it.
- **The API never reads these headers.** Cross-site requests that carry the
  proxy's cookie cannot act as the user.
- **NPM is the only way in.** The PaperDotNet container publishes no port.
  Forwarded headers (scheme, host, client address) count only from NPM's
  address.
- **A proxy sign-in lasts a day** (`PROXY_SIGN_IN_LIFETIME`). Then the
  browser goes through the proxy again, so users removed at the proxy lose
  access and group changes arrive.

**Things to avoid in NPM:**
- **An access list on the whole proxy host.** NPM would remove the
  `Authorization` header, so tokens could not reach PaperDotNet. Protect only
  `/auth/proxy/`, as the Advanced configs here do.
- **"Block Common Exploits".** It answers 403 to query strings that
  contain, for example:
  - `concat(`, which OData filters can contain;
  - `union … select (`, which search terms can contain;
  - `=http://`, as in an unencoded loopback `redirect_uri`.
- **"Cache Assets".** PaperDotNet sets its own cache headers (hashed files
  are cached for a year, everything else is revalidated).

## Setup

### 1. Configure and start

```bash
cd samples/nginx-proxy-manager
cp .env.example .env    # host names, admin password, PROXY_SECRET; for Authelia: AUTH_HOST, COOKIE_DOMAIN, secrets
docker compose up -d    # builds the PaperDotNet image from this repository on the first run
```

`COMPOSE_PROFILES` in `.env` selects what starts:
- `npm`: NPM and PaperDotNet.
- `npm,authelia`: the same plus Authelia.
- empty: only PaperDotNet, to use with [your existing NPM](#using-the-npm-you-already-run).

The DNS names (`PAPERDOTNET_HOST`, and `AUTH_HOST` for Authelia) must point
to this server, and ports 80 and 443 must be reachable for Let's Encrypt.
Create `PROXY_SECRET` with `openssl rand -hex 32`.

### 2. Create the proxy host in NPM

Open NPM at `http://<server>:81`. The first start asks you to create an NPM
admin account. Then go to **Hosts → Proxy Hosts → Add Proxy Host**:

| Tab | Setting |
|---|---|
| Details | Domain names: `PAPERDOTNET_HOST`. Scheme `http`, forward hostname `paperdotnet`, port `8080`. **Cache Assets: off. Block Common Exploits: off.** Websockets Support: either (not used). Access List: **Publicly Accessible** |
| SSL | Request a new certificate. **Force SSL: on.** HTTP/2: on. HSTS: as you like |
| Advanced | Paste one of the files below and replace `PROXY_SECRET` with the value from `.env` |

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
3. Set `PROXY_LOGOUT_URL=https://AUTH_HOST/logout` in `.env`, so signing out
   of PaperDotNet signs you out of Authelia too, then run
   `docker compose up -d`.
4. Users and groups live in
   [`authelia/users_database.yml`](authelia/users_database.yml). Both demo
   users have the password `change-me-demo`.
5. Two-factor sign-in: set `policy: two_factor` in
   [`authelia/configuration.yml`](authelia/configuration.yml). Registration
   e-mails are written to a file: `docker compose exec authelia cat /var/lib/authelia/notification.txt`.

### 3. First sign-in, groups and roles

1. Open `https://PAPERDOTNET_HOST`. The proxy asks you to sign in.
   - The proxy user **`admin`** is PaperDotNet's first administrator. Users
     with the same name are the same account.
   - Every other user gets an account as a Member on first sign-in
     (`CREATE_USERS=true`).
2. Groups the proxy names (`Admins`, `Finance`) are created at the first
   sign-in that names them (`CREATE_GROUPS=true`). The signed-in user joins
   them.
3. As `admin`, give roles to groups (Admin → Roles). For example, assign
   *Administrator* to the group *Admins*. Then everyone the proxy puts in
   *Admins* is an administrator.
4. With `GROUP_SYNC=Sync`, users leave proxy-created groups at their next
   sign-in through the proxy once the proxy stops naming them.
   - Groups you create yourself in PaperDotNet are never changed by the
     proxy; they only gain members it names.
   - The last administrator is never removed.
5. Removing a user at the proxy ends their web sign-in within
   `PROXY_SIGN_IN_LIFETIME`. Their API tokens keep working until you
   **disable** the user in PaperDotNet, which also ends everything else at
   once.

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
3. Access-list mode with groups only: copy
   [`npm/http_top.conf`](npm/http_top.conf) to
   `/data/nginx/custom/http_top.conf` in NPM's data folder, or append it to
   the file if you already have one. Then restart NPM.
4. Continue with [step 2](#2-create-the-proxy-host-in-npm).

## Checking the setup

```bash
H=docs.example.com
curl -sI "https://$H/auth/proxy/sign-in" | head -n 1      # 401 (access list) or 302 to Authelia
curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' -H 'Remote-User: admin' \
  "https://$H/connect/authorize?client_id=paperdotnet"    # a redirect to /auth/proxy/sign-in: the header alone does nothing
curl -s "https://$H/.well-known/openid-configuration" | grep -o '"issuer":"[^"]*"'   # https://docs.example.com/
curl -sN -H "Authorization: Bearer pdn_…" "https://$H/v1.0/me/events" | head -n 2   # "event: connected" at once
docker compose logs paperdotnet | grep -i "warn.*proxy"  # nothing: secret set, forwarded headers limited to NPM
```

**How this sample was tested:**
- The NPM config was run in nginx with NPM's own `proxy.conf`, http
  settings and forced-SSL block, in front of PaperDotNet, with Authelia
  4.39. The test did not use the NPM container.
- What passed:
  - sign-in with the access list and with Authelia;
  - name, e-mail and groups taken over;
  - the token exchange;
  - the API;
  - live events and MCP;
  - the issuer in OIDC discovery;
  - forged `Remote-User` headers without the secret were ignored.
- `compose.yml` was checked with `docker compose config`.

## Files

| File | What it is |
|---|---|
| `compose.yml` | NPM (profile `npm`), PaperDotNet, Authelia (profile `authelia`) on the network `paperdotnet-proxy` |
| `.env.example` | Host names, secrets, NPM address, group and sign-in settings, profiles |
| `npm/advanced-access-list.conf` | Advanced config for mode A: `/auth/proxy/` with the access list |
| `npm/advanced-authelia.conf` | Advanced config for mode B: `/auth/proxy/` through Authelia |
| `npm/http_top.conf` | Groups per access-list user (mode A) |
| `authelia/configuration.yml` | Authelia: file users, one access rule, session cookie for `COOKIE_DOMAIN` |
| `authelia/users_database.yml` | Demo users `admin` (Admins) and `alice` (Finance) |
