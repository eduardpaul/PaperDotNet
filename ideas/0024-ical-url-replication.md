# 0024: Replicate events from an iCalendar URL into any calendar list

- **Status:** mapped
- **Area:** Calendar / Integrations
- **Date:** 2026-10-02
- **Mapped to:** [features.md](../docs/features.md): CAL-07 (P7 backlog; API, SDK and web UI)

## The idea

As a user, I want to add an iCalendar URL to any calendar list and obtain
the events directly from that source, so that I can see an external calendar
in PaperDotNet without maintaining a second copy manually.

For example, paste Google Calendar's **Secret address in iCal format** into
a list. PaperDotNet fetches the feed and replicates its events into that list.
The feature also works with other providers publishing compatible feeds.
This is a proposed feature, not implemented behavior.

## Proposed first release

**CAL-07: iCalendar URL replication** covers adding multiple source URLs, initial
import, scheduled refresh, Refresh now, status, pause and source removal.
The external feed is the source of truth. No Google API project, OAuth,
calendar picker, outbound writes or two-way conflict resolution is needed.

Any list supporting the built-in `event` content type can receive a source,
including custom lists rather than only Calendar-template lists. There are
no additional workspace-owner, Google-owner or shared-calendar requirements;
use ordinary list configuration and item-access permissions. The same URL
can populate multiple lists independently. Both are first-release requirements:

- **Multiple iCalendar URLs can be added to the same calendar list.**
- **The same iCalendar URL can be used in different calendar lists.**

Each list/source subscription has independent event identities, refresh state
and controls. Refreshing, pausing or removing one subscription cannot change
events imported by another subscription, even when they share a URL or UID.

### User flow

1. Open **List settings → Calendar sources**, paste an HTTPS iCalendar URL
   and save. Repeat to add more URLs to the same list. Fetch and validate each
   source before activating its subscription.
2. Events appear in the list and its calendar views, marked as imported.
   Imports inherit the destination list's normal permissions.
3. Refresh automatically on a proposed 15-minute schedule, plus **Refresh
   now**. Show last success, created/updated/removed counts and fetch errors per source.
4. Pause, replace an expired URL, or remove an individual source. Removing it stops
   refreshing and retains imported events as local items. Replacing the URL
   for the same feed preserves identities; switching to a different feed is
   an explicit replacement that keeps the old events as local items.

### Replication behavior

- Import `VEVENT` title, description, location, start/end and all-day values.
  Preserve exclusive all-day end dates, time zones, supported recurrence
  rules and cancelled/moved occurrences. Store series and exceptions rather
  than materializing an unlimited number of future occurrences.
- Identify replicas by subscription plus `UID`, and `RECURRENCE-ID` for
  exceptions. Repeated downloads, retries and resuming a subscription update
  the same items. Do not match local events by title/time or collide with a
  manual `.ics` import that happens to have the same UID.
- The source controls replicated fields and recurrence. Mark them as
  source-managed in the API/UI while subscribed; users edit them in the
  source calendar. Local metadata remains editable. Locally created events
  remain independent and editable.
- Apply explicit cancellations. After a successful, complete, valid snapshot,
  source-linked events absent from the feed leave the active calendar (move
  to the recycle bin). Only that source's replicas are reconciled. Returning
  events restore without duplicates. Reconcile removed exceptions as well
  as newly added exceptions.
- Failed downloads, invalid/truncated feeds, exceeded limits or partial
  imports cannot trigger removal; retain the last successful replica.
  Validate/stage the snapshot first and reconcile deletions only after its
  supported components are durably processed. A valid empty feed can remove
  previously imported events.
- Replicate what the URL supplies. Providers may publish only a rolling date
  window or delay updates; events outside the feed are not guaranteed to
  remain in the active replica. Refresh now cannot bypass provider caching.
  Report unsupported components and retain their previous replicas when
  identities are known; skip deletion reconciliation when parsing cannot
  establish safe identities.
- Invitations/RSVP, alarms, attachments, conferencing and `VTODO` task imports
  are outside v1. This feature copies event data without sending invitations.

## Implementation approach

Reuse CAL-04's Ical.Net parser/import and CAL-01…03's events, recurrence and
views. Add subscription behavior within Calendar without a Google SDK.
The existing importer remembers UIDs in `EventSource`, supports RRULE,
EXDATE and moved occurrences, but is not yet a safe periodic replicator:

- Add tenant-scoped source settings, subscription-scoped identities, snapshot
  state and status, with SQLite/PostgreSQL migrations. Preserve manual `.ics`
  import behavior and isolate identical UIDs from different sources.
- The importer currently limits components with `Take`, writes items while
  processing, and adds EXDATEs without removing obsolete exceptions. Detect
  limits before reconciliation, stage snapshots, reconcile recurrence state,
  and make interrupted imports resumable/idempotent.
- Fetch on the server with bounded size, timeout and redirects. Reuse existing
  outbound-request protections to validate public HTTPS destinations and
  resolved addresses at every redirect. Encrypt secret URLs with existing
  Data Protection; omit them from routine responses, logs, workflow payloads
  and portable templates.
- Honor HTTP ETag/Last-Modified where available. A 304 keeps the current
  snapshot; HTTP/network failures retain events and show an error. Serialize
  refreshes per source with existing durable execution/retry patterns.
- Expose a refresh activity and built-in scheduled/manual replication workflow
  through the existing engine (ADR-0036). Use the normal tenant/list
  authorization and validation pipeline; no OAuth-owner model or separate
  scheduler is required.
- Add list-scoped source/status/refresh endpoints under `/v1.0` and generated
  SDK operations. The web UI consumes that SDK and ordinary list permissions.

## Delivery slices

1. **Source and fetch:** list setting, secret URL storage, validation, bounded
   fetch, status and initial import using existing iCalendar support.
2. **Repeatable replication:** source identities, event/exception reconciliation,
   safe removal/restoration, conditional fetch and durable retry.
3. **Usable release:** scheduled/manual refresh workflow, SDK and list-settings
   UI, source-managed fields, pause/remove, documentation and tests.

These are slices of CAL-07. The first release includes repeated refresh;
a one-time file import does not satisfy the requirement.

## Acceptance criteria

1. A user can add a Google secret iCal URL or another compatible HTTPS feed
   to any event-capable list with ordinary list permissions and no Google
   API setup or calendar-ownership check. Multiple URLs can be added to the
   same list, and the same URL can be added to different lists.
2. Initial/later refreshes and crash recovery produce no duplicates. The same
   URL populates two lists independently; matching UIDs from manual imports
   do not overwrite unrelated items. Two sources in one list with matching
   UIDs remain independent; refreshing, pausing or removing either source
   leaves the other source and subscriptions in other lists unchanged.
3. All-day dates, DST-sensitive series, removed recurrence rules, moved
   instances and added/removed cancellations match the feed.
4. Complete snapshots reconcile only source-linked replicas. Partial,
   oversized/invalid feeds and HTTP failures preserve existing events.
   Returning events restore without duplication.
5. Imported fields are source-managed; local events and metadata remain
   intact. Pause/removal stop fetching; removal retains local copies.
6. The desktop/phone UI shows last success and errors, offers Refresh now and
   URL replacement, and never exposes the secret in routine responses or logs.

Use fixture feeds and a fake HTTP transport for mapping, redirects, empty/
partial feeds, UID collisions and retry recovery; integration tests on both
databases and Playwright for the user flow. A manual smoke test with a
disposable Google calendar's secret URL verifies compatibility without
requiring Google credentials in normal CI.

## References

- [Existing iCalendar importer](../src/Modules/Calendar/PaperDotNet.Calendar/Features/ICalendar.cs)
- [Calendar data](../src/Modules/Calendar/PaperDotNet.Calendar/Data/CalendarDbContext.cs)
- [Workflows](../docs/workflows.md)
- [Google: view a calendar in other applications](https://support.google.com/calendar/answer/37648)
- [RFC 5545: iCalendar](https://www.rfc-editor.org/rfc/rfc5545)
