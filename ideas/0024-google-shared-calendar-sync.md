# 0024: Two-way sync between a workspace calendar list and a Google shared calendar

- **Status:** mapped
- **Area:** Calendar / Integrations
- **Date:** 2026-10-02
- **Mapped to:** [features.md](../docs/features.md): CAL-07, CAL-08, CAL-09 (P7 backlog; includes SDK and web UI)

## The idea

As a user, I want to connect a specific Calendar list in a PaperDotNet
workspace to an existing Google shared calendar, so that events created,
edited or deleted on either side stay in sync for the team.

**Two-way sync is required for the first release**, including edits and new
events made in Google. This is a feature proposal, not implemented behavior.

## Why / problem it solves

The workspace remains the team's home for its data, while people can use
Google Calendar on their phones and desktops without maintaining two schedules.
The connection belongs to one list, not the user's combined calendar or every
calendar in a workspace.

CAL-04 already provides a list-scoped read-only `.ics` feed. Google can
subscribe to it as a separate calendar, but that does not write events into
an existing shared calendar or bring Google's edits back. CAL-05 (CalDAV)
does not supply this Google API connector either.

## Proposed first release

| Feature | Deliverable |
|---|---|
| CAL-07 | Authorize Google access and connect one Calendar list to one existing writable Google calendar, including a calendar shared with the authorizing account. |
| CAL-08 | Initial merge and ongoing two-way create/update/delete for supported events, recurring series and occurrence exceptions. |
| CAL-09 | Sync status, per-event errors and conflicts, manual retry, pause, reconnect and disconnect. |

One active connection per list and per Google target calendar within a tenant
keeps ownership clear. Do not support fan-out or chains of connected lists in
this release. The same external calendar must not be deliberately connected
to multiple tenants; cross-install connections cannot be reliably detected
and must be documented as unsupported.

### User flow and permissions

1. A user with Manage access to the workspace and selected list opens
   **List settings → Integrations → Google Calendar**.
2. They authorize a Google account with offline access. This is authorization
   to Google's API, separate from PaperDotNet sign-in or external OIDC login.
3. They choose an existing calendar where Google reports `writer` or `owner`
   access. A read-only shared calendar cannot be connected. PaperDotNet does
   not create calendars, change Google ACLs or require a Google Workspace
   administrator/service account.
4. A preview shows the source list, destination calendar, supported event
   counts, skipped events and the data-sharing boundary. Only explicit
   activation starts the initial merge.
5. Events subsequently sync in both directions. List settings show the last
   successful sync, pending work, conflicts and actions to retry or reconnect.

The Google grant is owned by the authorizing PaperDotNet user; list managers
can manage the connection but cannot retrieve the tokens. Recheck that user's
active status and Manage access and the Google grant before background work.
Loss of either pauses the connection and requires an authorized reconnect.

Google calendar access is independent of PaperDotNet ACLs: everyone who can
read the Google calendar may see exported content, and imported events inherit
the list's default permissions. Explain this in the preview. Exclude items
with unique/restricted permissions in v1, even if the connecting user can
read them; show a skipped reason. If an exported item becomes restricted,
remove its linked Google copy while authorized and retain a suppression
record so it is not imported back. If cleanup cannot run, surface that the
external copy remains; pausing or revoking access cannot recall existing data.

### Event mapping and boundaries

| PaperDotNet | Google Calendar | Policy |
|---|---|---|
| `title` | `summary` | Two-way |
| `description` | `description` | Two-way plain text; define normalization before hashing |
| `location` | `location` | Two-way |
| `start`, `end` | `start.dateTime`, `end.dateTime` | Preserve instants and the applicable IANA time zone |
| `allDay`, date boundaries | `start.date`, `end.date` | Preserve dates and exclusive end; never shift via a viewer's time zone |
| Event recurrence rule and time zone | Series recurrence and time zone | Sync series masters, not an unbounded set of expanded occurrences |
| Cancelled/moved occurrence | Cancelled/modified recurring instance | Key by master plus original start, including date identity for all-day events |

Limit the initial version to Google's ordinary `default` event type and
Calendar-template lists containing the built-in `event` content type. Tasks,
custom calendar-shaped lists, birthdays, working location, focus time,
out-of-office, attachments and conferencing are outside the sync contract.
Unsupported recurrence shapes or unrepresentable events are reported and
left unchanged, not flattened or silently truncated.

PaperDotNet attendees are user IDs, while Google attendees are email guests
with RSVP/organizer behavior. Do not map attendees, invitations, responses or
reminders in v1. Preserve existing Google-only properties on patch; new
exports have no guests and no reminders. Use `sendUpdates=none` and do not
promise suppression of every possible Google notification. Creating a Google
event in PaperDotNet does not send invitations or reproduce its guest list.

### Initial merge, identity and deletion

- Import supported existing Google events into the list and export eligible
  existing list events. Distinct events remain distinct, even if their titles
  and times match; do not guess identity from similarity. Explain this in the
  preview, especially when both sides already contain copies of a schedule.
- Persist tenant-scoped links between connection, list item/series and Google
  event ID (and original occurrence identity). Store last agreed canonical
  values, local version, Google ETag, cursor, origin and deletion state.
  Keep this separate from `EventSource`, which tracks iCalendar import UIDs.
- Use a stable Google-compatible client-assigned ID for newly exported events
  and persist import identity transactionally. Retrying after a crash between
  a remote write and local acknowledgement must find the same event, not
  create another. Repeated pages and item notifications are idempotent.
- Deleting a linked event deletes its counterpart; a Google deletion moves
  the list item to the recycle bin. An occurrence cancellation affects only
  that occurrence. Retain tombstones to prevent automatic resurrection.
  Explicit restoration is a new sync intent; a provider's deleted ID may
  require a new remote ID. Delete-versus-edit is a conflict, not an implicit
  winner. Unsupported/unlinked events are never deleted by reconciliation.
- Pause and disconnect retain events on both sides. Disconnect stops work
  and releases the stored grant when no other connection uses it; it does
  not delete the shared calendar or bulk-delete its contents. Reconnecting
  the same pair should reuse retained links; changing the target requires a
  new preview and must not reuse the old target's IDs.

### Incremental sync and conflicts

Use Google's paginated incremental `events.list` with `syncToken`, including
deletions. Persist a new cursor only after every page's changes are durably
applied or recorded as pending conflicts/errors. A 410 invalid cursor triggers
a full reconciliation, retaining links. Never infer deletions from a failed,
partial or access-denied scan, or delete unrelated Google events.

Item changes should queue prompt outbound work through the existing outbox.
Poll Google on a proposed five-minute schedule, plus **Sync now**; this is a
normal-service target, not a guarantee during outages or quota throttling.
Push notification channels and public callback hosting are deferred.

Compare both sides against their last agreed mapped values. If only one side
changed, apply it with conditional writes (Google ETag and PaperDotNet version).
If both changed to the same values, acknowledge convergence; otherwise record
a conflict and pause that event while other events continue. This includes
delete-versus-edit. Never choose a winner using cross-system timestamps.
The manager sees both versions and chooses **Use PaperDotNet** or **Use
Google**; recheck versions before resolving. Preserve unmapped Google fields.
Origin plus canonical-value comparison prevents write echoes without ignoring
later genuine edits. Recurrence and ACL changes also require reconciliation.

## Implementation approach and gaps

Build an optional in-process connector, off by default, on the extension
SDK/contracts (EXT-06). No extra runtime service or required database beyond
SQLite/PostgreSQL. Use Google's maintained .NET Calendar/OAuth libraries;
review and register dependencies/licenses before implementation, rather than
hand-rolling OAuth. Store refresh tokens encrypted with the existing Data
Protection infrastructure; redact them from API responses, workflow data,
logs and portable templates. Self-hosters supply a Google Cloud OAuth client,
Calendar API enablement, registered redirect URI and appropriate consent
configuration; Google app verification/testing-mode token restrictions need
deployment documentation.

Candidate scopes: `calendar.calendarlist.readonly` to choose calendars and
`calendar.events` to modify an existing shared calendar. Verify the minimum
working scope set against Google's current policy during a connector spike;
the owned-calendar-only scope does not cover calendars merely shared with
the account. Bind OAuth state to tenant, user and destination list, protect
against callback replay, and validate Google's reported calendar access.

Use tenant-owned connector configuration, event links and cursors with EF
Core and migrations for both providers. Serialize work per connection with
the existing durable execution/lease patterns; never hold a database
transaction open across a Google HTTP call. Handle quota-specific 403/429
and transient errors with backoff; revoked grants and permission errors need
reconnect. Keep a durable pending record for operations that partly succeeded.

Expose connector activities/triggers and a built-in sync/reconciliation
workflow through the existing workflow engine (ADR-0036), with item and
schedule triggers. Connector tables hold synchronization domain state;
retry/run scheduling stays in the existing engine/outbox. Do not introduce
another queue, workflow engine or independent scheduler.

Existing foundations: CAL-01…04, LST-16, IAM-07, EVT-02/04, API-05 and
EVT-12. Important gaps to address before coding the connector:

- Calendar recurrence/exception endpoints currently save their own data
  directly; item events alone cannot reliably capture every calendar change.
  Add calendar change events/contracts for series and exceptions, including
  transactional publication, and retain periodic reconciliation for recovery.
- The connector must access recurrence/exception data through public
  SDK/contracts, not another module's DbContext. Add missing read/write
  contracts rather than calling the time-range API and losing series identity.
- Imported writes must use the normal list/calendar validation and ACL
  pipeline under a scoped authorized identity. Add narrowly scoped sync
  contracts if conditional/idempotent writes are missing.
- Add list-scoped connection/status/conflict endpoints under `/v1.0`, scopes,
  ETags and generated SDK operations; the web UI consumes only that SDK.
  Portable templates may carry non-secret settings but must require reconnect.

## Delivery slices

1. **CAL-07 / feasibility:** OAuth and writable-calendar selection; test
   a genuinely shared calendar, scope/refresh behavior and series mapping;
   add missing calendar contracts/events and both-provider persistence.
2. **CAL-08 / sync:** preview/initial merge, identity links, conditional
   two-way writes, recurrence/exception round trips, deletion/restoration,
   incremental polling, loop protection and crash recovery.
3. **CAL-09 / usable release:** list-settings UI, status, conflict resolution,
   pause/reconnect/disconnect, deployment documentation and end-to-end tests.

All three slices form the first usable release. Recurrence and conflict
handling are correctness requirements, not optional follow-up work. Schedule
after a shared-calendar spike confirms provider interoperability; do not mark
the backlog as implemented or commit to a delivery date yet.

## Acceptance criteria

1. A manager connects exactly the chosen list to a shared Google calendar
   where the authorizing account is a writer; other lists/calendars are unchanged.
   Read-only grants and cross-tenant resource IDs are rejected.
2. After preview/activation, supported existing events merge; new events and
   changes to mapped fields in either product reach the other. Repeated sync,
   duplicate messages and a crash after remote creation produce no duplicates.
3. All-day and multi-day events preserve dates; timed recurring events retain
   their local hour across DST. Series edits, moved instances and single-instance
   cancellations round-trip without duplicating masters or occurrences.
4. Linked deletion propagates without resurrection; deleting an event on one
   side while editing it on the other produces a resolvable conflict. Unrelated
   or unsupported Google events and unmapped properties remain intact.
5. Concurrent incompatible edits pause only that event. Both snapshots are
   visible to an authorized manager and resolution uses version checks.
6. Quota throttling, lost refresh grants, expired sync tokens, partial pages
   and process restarts recover or show an actionable status; none causes a
   destructive full-calendar replacement or hidden data loss.
7. Restricted items are excluded and newly restricted exported copies are
   cleaned up or flagged. Disabling the owner or removing required access
   pauses work. Tokens never appear in the SDK, logs or provisioning export.
8. Pause/disconnect stop writes while retaining events; reconnecting the same
   pair does not repeat the initial import. **Sync now** and status work in the
   first-party UI on desktop and phone.

Test mapping and recovery with a fake Google transport, integration tests on
SQLite and PostgreSQL, and Playwright for the user flow. Add opt-in real-Google
smoke tests using two accounts and a disposable shared calendar to establish
actual shared-calendar/recurrence behavior without making CI require secrets.

## Examples / references

- [Calendar implementation](../src/Modules/Calendar/PaperDotNet.Calendar/Features/CalendarEndpoints.cs)
- [Calendar data and recurrence identity](../src/Modules/Calendar/PaperDotNet.Calendar/Data/CalendarDbContext.cs)
- [Sync API](../docs/collaboration-and-sync.md), [workflows](../docs/workflows.md)
- [Google incremental sync](https://developers.google.com/workspace/calendar/api/guides/sync)
- [Google recurring events](https://developers.google.com/workspace/calendar/api/guides/recurringevents)
- [Google API scopes](https://developers.google.com/workspace/calendar/api/auth)
- [Google event resource](https://developers.google.com/workspace/calendar/api/v3/reference/events)

## Remaining design choices

Validate the supported RRULE/exception subset and normalization rules in the
spike. Define tombstone/link retention and how managers transfer a connection
to another Google grant. A future release may add guest/invitation mapping,
push notifications or more providers; none changes the two-way requirement.
