# Accounts and preferences

Account lifecycle (IAM-14), user preferences (PLT-17) and organization
defaults (PLT-18). Design: [ADR-0030](adr/0030-account-lifecycle-and-preferences.md).

## Users

Needs `user.manage` (administrators).

| Request | What it does |
|---|---|
| `PATCH /v1.0/users/{id}` `{ "displayName", "email", "isDisabled" }` | Updates a user. Disabling ends the user's sessions, OAuth tokens and API tokens. |
| `POST /v1.0/users/{id}/password` `{ "password" }` | Sets a new password (admin reset). It also unlocks the account and ends sessions and OAuth tokens. API tokens stay. |
| `DELETE /v1.0/users/{id}` | Deletes a user. See below. |

**Deleting** keeps the user's row so that authors, history and person values
still resolve, but it is anonymized:
- **Identity:** the user name becomes `deleted-{id}`, which frees the name;
  e-mail, password, passkeys and external logins are removed.
- **Memberships:** the user leaves every group and role.
- **Tokens:** every token and session ends.
- **Other modules:** permission grants and workspace memberships are removed
  in the background (event `PrincipalDeleted`).

Deleted users no longer appear in `/v1.0/users`.

Nobody can disable or delete their own account (`409 cannotChangeSelf`).

## Your password

`POST /v1.0/me/password` `{ "currentPassword", "newPassword" }`. A wrong
current password gives `400` with an error on `currentPassword`. Other sign-in
sessions and OAuth refresh tokens end; API tokens stay.

## Groups and roles

| Request | Scope | What it does |
|---|---|---|
| `PATCH /v1.0/groups/{id}` `{ "name", "description" }` | `group.manage` | Renames a group (ETag, unique names) |
| `DELETE /v1.0/groups/{id}` | `group.manage` | Deletes a group with its memberships and role assignments. Its permission grants are removed in the background |
| `PATCH /v1.0/roles/{id}` `{ "name", "description", "scopes" }` | `role.manage` | Changes a role; the new scopes apply at once. Built-in roles keep their names, and Administrator always has every scope |
| `DELETE /v1.0/roles/{id}` | `role.manage` | Deletes a custom role and its assignments |
| `DELETE /v1.0/roles/{id}/assignments/{assignmentId}` | `role.manage` | Removes a role from a user or group |

**The last administrator is protected.** A change that would leave no
enabled user with the Administrator role, directly or through a group, is
refused with `409 lastAdministrator`. This covers:
- disabling or deleting a user;
- removing a role assignment;
- removing a group member;
- deleting a group.

## Sign-in through a reverse proxy

Behind Authelia, Authentik, oauth2-proxy or Nginx Proxy Manager, the proxy
can sign users in. This is IAM-15, designed in
[ADR-0031](adr/0031-reverse-proxy-sign-in.md) and
[ADR-0045](adr/0045-generic-reverse-proxies.md).

```json
"Auth": { "ReverseProxy": {
  "Enabled": true,
  "TrustedProxies": ["172.18.0.2"],
  "Secret": "a long random value the proxy sends", "SecretHeader": "X-PaperDotNet-Proxy",
  "UserHeader": "Remote-User", "EmailHeader": "Remote-Email", "NameHeader": "Remote-Name", "GroupsHeader": "Remote-Groups",
  "CreateUsers": true, "GroupSync": "Add", "CreateGroups": false,
  "RefreshTokenLifetime": "1.00:00:00", "LogoutUrl": "https://auth.example.com/logout" } },
"ForwardedHeaders": { "Enabled": true, "KnownProxies": ["172.18.0.2"], "ForwardLimit": 1 }
```

**Where the headers count:**
- Only from a trusted proxy: the direct peer, not `X-Forwarded-For`.
- Only with the `Secret`, when one is set. The proxy sends it only where it
  authenticated the user, so mistakes in the proxy's routing fail closed.
  Without a secret, the server logs a warning at startup.
- Only at `/auth/proxy/sign-in` and `/connect/authorize`. Signed-out
  browsers are sent to `/auth/proxy/sign-in?returnUrl=…`, which is the only
  path the proxy needs to protect. Since it is outside `/connect/`, browsers
  never send basic-auth credentials to the OAuth endpoints. There the
  headers start the sign-in session; the API keeps using tokens. When the
  proxy names nobody, the path falls back to the password sign-in.

**Users:** unknown users are created without a password as Members. Their
name and e-mail follow the headers.

**Groups:** users join existing groups named in the groups header
(comma-separated, up to 100).
- `CreateGroups` creates missing groups as *proxy groups*.
- With `GroupSync: "Sync"`, users also leave proxy groups the proxy no
  longer names. Groups created in PaperDotNet only ever gain members.
- The last administrator is never removed.

**How long a proxy sign-in lasts:** at most `RefreshTokenLifetime`, then
the browser goes through the proxy again.
- Refreshing does not extend it.
- Name, e-mail, groups, and whether the proxy still admits the user are
  read again then.
- `LogoutUrl` is where signing out of a proxy session sends the browser.
  Without it, the proxy would sign the user straight back in.

**Local sign-in:** with `"Auth": { "LocalSignIn": false }`, only the
proxy signs people in.
- Passwords and passkeys stored in PaperDotNet are refused, as is the
  password grant (`403 localSignInDisabled`). This includes the first
  administrator's password.
- Nobody can skip the proxy's second factor, bans or removed users.
- API tokens and client credentials keep working.
- Turn it on again for a moment for a break-glass sign-in.

**Forwarded headers** (scheme, host, client address) count only from
`ForwardedHeaders:KnownProxies`, or else from the sign-in proxies.
`ForwardLimit` is the number of proxies in a chain. Event streams send
`X-Accel-Buffering: no`, and live events send a keep-alive every 30 s, so
nginx-based proxies pass them on at once. `Jobs:LiveEventsKeepAlive` is checked
at startup and must be between 1 and 4294967294 milliseconds.

A tested setup for Nginx Proxy Manager, with an NPM access list or Authelia, is in
[samples/nginx-proxy-manager](../samples/nginx-proxy-manager/README.md).

## Preferences

`GET /v1.0/me/preferences` returns the effective values and lists the ones
that are `inherited`:

```json
{
  "language": "de-DE", "timeZone": "Europe/Berlin", "dateFormat": "dd.MM.yyyy",
  "timeFormat": "24h", "numberFormat": "de-DE", "theme": "dark",
  "documentLanguages": "deu+eng", "inherited": ["language", "timeZone", "numberFormat", "documentLanguages"]
}
```

| Preference | Values | Built-in default |
|---|---|---|
| `language` | Culture name (`en`, `de-DE`) | `en` |
| `timeZone` | IANA time zone | `UTC` |
| `dateFormat` | Pattern of `d`, `M`, `y` with `.`, `/`, `-` or spaces | `yyyy-MM-dd` |
| `timeFormat` | `24h` or `12h` | `24h` |
| `numberFormat` | Culture name | `en` |
| `theme` | `system`, `light` or `dark` | `system` |
| `documentLanguages` | Tesseract codes joined with `+` (`deu+eng`) | `eng` |

**Changing preferences:** `PATCH /v1.0/me/preferences` takes a JSON merge
patch with an optional `If-Match`. A property set to `null` goes back to the
inherited value. Unknown or invalid values give `400`, with one error per
property.

**Organization defaults:** `GET /v1.0/organization/preferences` (anyone) and
`PATCH /v1.0/organization/preferences` (`organization.manage`). They sit
between each user's own values and the built-in defaults.

**Where they are used:**
- **Notifications:** quiet hours and the daily digest hour follow the user's
  time zone. Notification settings no longer have their own `timeZone`.
- **Recurring events:** they use the caller's time zone when `timeZone` is
  not given.
- **OCR:** libraries without their own `ocrLanguages` use the organization's
  `documentLanguages` (`ocrLanguagesInherited: true` in `…/documentSettings`).
  An empty `ocrLanguages` goes back to the default.
- **Other modules:** read effective values with `IUserPreferences`
  (Identity.Contracts).

The UI settings (language, formats, theme) are stored for a future UI; the
API itself always uses ISO dates and invariant numbers.
