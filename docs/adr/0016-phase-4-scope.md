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

## Consequences

- Existing tenants keep their task content type as it was (built-in content
  types are not overwritten); new fields such as `startDate` appear for new
  tenants, or when an admin adds them.
- Phones and desktop apps get read-only calendars until CalDAV arrives.
