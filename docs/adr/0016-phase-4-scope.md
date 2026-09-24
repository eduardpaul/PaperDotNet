# ADR-0016: Tasks and Calendar on the SDK, CalDAV later

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Phase 4 brings tasks, calendar and notifications (TSK, CAL, NTF). EXT-06 asks
for the built-in apps to be built on the public SDK. CalDAV (CAL-05) would need
a large protocol implementation, and no mature MIT/Apache CalDAV server library
exists for .NET.

## Decision

- **Tasks and Calendar are SDK-only modules** like Documents (ADR-0015): tasks
  and events are list items; each module owns its content type and list
  template (registered like built-in ones) and keeps what fields cannot express
  in its own schema (`ExtensionDbContext`): checklists, links, recurrence rules.
- **Task links** (subtask, blocked by, document) are rows from a task to
  another item, checked for cycles; subtasks have one parent. Linked items are
  shown only when the caller can read them.
- **Recurring tasks** use RRULE (RFC 5545, Ical.Net). Completing an occurrence
  creates the next one (an `ItemUpdated` subscriber) and moves the rule to it,
  so redelivered events do nothing.
- **Cross-list views** (`/v1.0/me/tasks`) query every task list the caller can
  read through `IListItemStore`, one OData query per list, merged by due date.
  Fine for self-hosted sizes; an index table can come later if needed.
- **CalDAV moves to P5** with WebDAV (API-10) and CardDAV (CAL-06), so one DAV
  layer serves all three. Phase 4 offers iCal import/export and read-only feeds.
- **Notification channels in Phase 4:** in-app and webhook. Email (MailKit)
  and ntfy/Gotify follow in P5.

## Calendar (4b)

- Event times are normalized by a before-receiver (all-day events span whole
  UTC days; a missing end is start + 1 hour; end before start is rejected).
- A series is the event item plus an RRULE and an IANA time zone; expansion
  uses Ical.Net, so local times survive DST changes. Cancelled occurrences and
  moved ones (their own event items) are stored as occurrence changes.
- Time ranges are at most 366 days and 2000 entries; due tasks appear as
  all-day entries.
- iCalendar export writes VEVENT (RRULE, EXDATE, RECURRENCE-ID, VTIMEZONE) and
  VTODO; import is idempotent by UID per list. Feeds are secret URLs
  (`/v1.0/calendarFeeds/{tenant}{secret}.ics`, only the hash is stored) that
  act as their owner with the owner's current access.

## Notifications (4c)

- A Notifications module on the SDK owns the inbox, preferences, follows and
  delivery. Other modules and extensions send through `INotificationSender`
  (`PaperDotNet.Notifications.Contracts`, part of the SDK); a deduplication key
  makes recurring jobs (reminders, digests) safe to run again.
- Reminders are recurring jobs in the modules that own the data: Tasks (due
  today, every 15 minutes) and Calendar (`reminderMinutes` before each
  occurrence, every minute, looking ahead 28 days).
- Follow alerts come from item events; each follower's access is checked in a
  scope as that follower, and the actor is never notified of their own change.
  Daily follows collect digest entries, sent at the user's digest hour.
- Webhooks are signed with a per-user secret (HMAC-SHA256 over
  `{timestamp}.{body}`, stored with Data Protection), sent only to https URLs
  that resolve to public addresses (checked at connect time against DNS
  rebinding), and retried by the dispatcher with its own backoff. The webhook
  client is therefore not an `IHttpClientFactory` client with the default
  resilience handler. Quiet hours delay webhook delivery; the inbox always
  receives.

## Consequences

- Existing tenants keep their task content type as it was (built-in content
  types are not overwritten); new fields such as `startDate` appear for new
  tenants, or when an admin adds them.
- Phones and desktop apps get read-only calendars until CalDAV arrives.
