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

Behind Authelia, Authentik or oauth2-proxy, the proxy can sign users in
(IAM-15, [ADR-0031](adr/0031-reverse-proxy-sign-in.md)):

```json
"Auth": { "ReverseProxy": {
  "Enabled": true,
  "TrustedProxies": ["172.18.0.0/16"],
  "UserHeader": "Remote-User", "EmailHeader": "Remote-Email", "NameHeader": "Remote-Name", "GroupsHeader": "Remote-Groups",
  "CreateUsers": true } }
```

**How it works:**
- **Where headers count:** only from a trusted proxy (the direct peer, not
  `X-Forwarded-For`), and only at `/connect/authorize`. There they start the
  sign-in session of the OAuth flow; the API keeps using tokens.
- **Users:** unknown users are created without a password as Members, and
  name and e-mail follow the headers.
- **Groups:** users are added to existing groups listed in the groups header.

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
