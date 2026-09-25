# ADR-0030: Account lifecycle and preferences

- **Status:** Accepted
- **Date:** 2026-09-25

## Context

Papermerge manages users, groups and roles fully and stores per-user
preferences; PaperDotNet could only create them (IAM-14, PLT-17, PLT-18).
- **Deleted users:** they are referenced everywhere, as authors, in item
  history, in person fields, as assignees and as members. Other modules
  reference users only by id, through contracts.
- **Time zones:** each module had its own setting (notification settings) or
  used UTC.

## Decision

- **Users are deleted by anonymizing, not by removing the row.**
  - **The row:** it gets `DeletedAt`, is disabled and loses its identifying
    data. The user name becomes `deleted-{id}`, so the name can be used
    again.
  - **Identity data:** its memberships and role assignments go in the same
    transaction.
  - **Other modules:** a `PrincipalDeleted` integration event
    (Identity.Contracts) lets them clean up in the background. Lists removes
    grants, which also resets delta sync; Workspaces removes memberships.
  - **Groups:** they are really deleted, and publish the same event.
- **Account changes end access:**
  - **Disable or delete:** revokes OAuth tokens and authorizations (OpenIddict
    `RevokeBySubjectAsync`) and API tokens.
  - **Password reset or change:** revokes OAuth tokens only.
  - **Sign-in sessions:** they carry the security stamp. A password change
    makes them invalid at the next authorization request.
  - **Scopes:** cached scopes are dropped for the tenant.
- **There is always an enabled administrator.** Every change that could
  remove the last one is checked first and refused with `lastAdministrator`.
  Nobody can disable or delete themselves.
- **Built-in roles** keep their names, and Administrator keeps every scope.
  Member's scopes can be changed.
- **One preference model with inheritance.** It lives in a table
  `identity.preferences`:
  - one row per user;
  - one row with `UserId = Guid.Empty` for the organization's defaults.
  - `null` means inherited: user, then organization, then built-in defaults.

  `IUserPreferences` (Identity.Contracts, part of the SDK) returns effective
  values, including in batches for jobs.
- **Modules use the preferences instead of keeping their own copies.**
  - Notifications drops its `timeZone` setting.
  - Calendar uses the caller's time zone as the default for recurrences.
  - Documents uses the organization's `documentLanguages` for libraries
    without their own OCR languages, whose setting is now nullable.
- **No search language preference.** Stemming already follows each document's
  language (per row on PostgreSQL), so a per-user search language would not
  change results.

## Consequences

- References to deleted users stay valid and show `deleted-…`. Personal data
  is removed, apart from the display name, which admins can change before
  deleting.
- **API change:** notification settings lose `timeZone`; clients set the time
  zone once in `/v1.0/me/preferences`.
- **Eventual cleanup:** other modules clean up after the event is handled.
  Until then, grants of a deleted principal match nobody, so there is no
  window in which they give access.
