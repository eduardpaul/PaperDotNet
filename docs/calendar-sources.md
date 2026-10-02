# Calendar sources (CAL-07)

Calendar lists can replicate events from one or more public HTTPS iCalendar
URLs. Google Calendar's **Secret address in iCal format** works without a
Google API project or OAuth connection. Other compatible feeds work too.

Open **List settings → Calendar sources**, enter a name and URL, and choose
**Add source**. The URL is checked before saving; the first refresh runs in
the background. The same URL can be added to different lists independently.
Each source in a list has separate identities, status and controls, even if
sources use matching event UIDs. A URL already added to that same list is
rejected to avoid an accidental duplicate subscription.

The destination may be any list with the built-in Event content type; it does
not need the Calendar template. Reading sources requires ordinary list read
access; configuring or manually refreshing them requires ordinary Manage
access to that list and the calendar scope. No additional workspace-owner
or external calendar-owner check applies.

The Calendar module enables the **Refresh calendar sources** built-in workflow
in the workspace. It refreshes active sources every 15 minutes in UTC, using
`calendar.refreshSources`. The activity optionally accepts a `sourceId` to
refresh just one source in its workspace. Workflow runs and retries use the
existing engine; disabling the workspace workflow stops automatic refresh.
**Refresh now** queues a long-running operation and leaves other sources alone.

The source owns title, description, location, start/end, all-day dates and
recurrence. These fields and recurrence are protected in the API and shown
as source-managed in the UI. Local metadata stays editable. Events created
locally in the list are independent and unaffected.

A complete, valid source snapshot updates its replicas and moves absent or
cancelled source events to the recycle bin. Returning events restore with
the same identity. Recurring series preserve their time zone, DST behavior,
EXDATE cancellations and moved instances. Removed exceptions are reconciled.
Source-backed events receive distinct UIDs when exported from PaperDotNet,
so two subscriptions with the same original UID remain distinct.

Fetch or parse failures retain existing events. A feed exceeding 10 MiB or
2000 VEVENT components is rejected instead of truncated. Unsupported time
zones, RDATE/EXRULE shapes or broken identities stop reconciliation. A valid
empty feed removes that source's active replicas. A failure applying an event
(e.g. a required custom list field) can leave earlier updates visible, but
never starts absent-event removal; the next refresh safely resumes. The
service does not promise a transaction spanning the Calendar and Lists modules.

A feed can omit old/future events or cache changes. Replication reflects what
the provider publishes, including its date window; Refresh now cannot force
Google to publish a newer snapshot. Invitations/RSVP, reminders, attachments,
conferencing and tasks are not imported.

**Pause** stops refreshing but keeps fields source-managed. **Remove source**
keeps its existing events as editable local items. **Replace URL** retains
identities for the same feed (such as a rotated secret); choose the different-
feed checkbox to keep the previous replicas local and start new identities.

Secret URLs are encrypted using tenant-bound Data Protection and omitted
from routine API responses, logs, workflow payloads and provisioning exports.
Protect/back up the deployment's Data Protection keys as usual. Portable
configuration exports source names; applying it warns that URLs must be
added again at the destination. HTTP validators reduce repeat downloads.
Only public HTTPS destinations are fetched, including at each redirect;
private/loopback addresses, HTTP redirects and unbounded responses are refused.

The list-scoped API is `/v1.0/workspaces/{workspaceId}/lists/{listId}/calendarSources`:

| Action | Endpoint |
|---|---|
| List sources (URLs omitted) | `GET /calendarSources` |
| Add source `{name,url}` | `POST /calendarSources` |
| Rename/pause/replace `{name?,paused?,url?,reset?}` | `PUT /calendarSources/{sourceId}` with `If-Match` |
| Remove source, retain events | `DELETE /calendarSources/{sourceId}` with `If-Match` |
| Queue a refresh | `POST /calendarSources/{sourceId}/refresh` → 202 operation |
| Item source and managed fields | `GET /items/{itemId}/calendarSource` |

Per-source leases serialize refresh and configuration changes across servers.
A lost worker's lease expires, while stable pre-saved item identities make
interrupted imports repeatable. The implementation lives in Calendar and
uses the public extension SDK for list writes and workflow activation.

Subscribed events stay in their subscription’s list. Remove the source first to
move its retained events to another list; moves within the same list are allowed.
