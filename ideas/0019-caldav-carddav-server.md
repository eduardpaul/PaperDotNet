# 0019: CalDAV / CardDAV server for tasks, calendar and contacts

- **Status:** mapped
- **Area:** Integrations / Calendar / Tasks
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): CAL-05, CAL-06

## The idea

Expose the Calendar and Tasks lists over **CalDAV**, and a future Contacts
list over **CardDAV**. Native apps can then read and write them with
two-way sync, including offline:

- iOS/macOS Calendar and Reminders
- Android (via DAVx⁵)
- Thunderbird, Outlook (via plugins), GNOME/KDE

## Why / problem it solves

- Tasks and calendar on every phone and desktop without building a mobile
  app first. It covers much of the offline need in idea 0006.
- Standard protocols, and very practical for self-hosters.
- Makes PaperDotNet a real calendar/task backend, not just a store.

## Examples / references

- RFC 4791 (CalDAV), RFC 6352 (CardDAV), RFC 5545 (iCalendar: VEVENT,
  VTODO, RRULE).
- Ical.Net (MIT) for iCalendar parsing and recurrence, see
  [dotnet-building-blocks.md](../docs/dotnet-building-blocks.md).
- Nextcloud, Radicale and Baïkal as reference implementations.

## Notes

<!-- Open questions to settle during review:
     - Mapping:
         - Calendar list = one CalDAV calendar collection
         - Event items ↔ VEVENT
         - Task items ↔ VTODO (status, priority, due, subtasks via
           RELATED-TO)
         - custom fields → X- properties?
     - Every Event/Task list (and every workspace calendar) appears as a
       separate collection; shared lists appear for all members.
     - Sync: ETags and sync-collection (RFC 6578) reuse the delta/ETag
       design from ideas 0003/0006.
     - Recurrence exceptions (RECURRENCE-ID), time zones (VTIMEZONE),
       alarms (VALARM) ↔ reminders (idea 0016).
     - Scheduling (iTIP/iMIP invitations by email): later.
     - Auth: app passwords/API tokens; per tenant URLs; well-known discovery
       (/.well-known/caldav).
     - Shares WebDAV plumbing with idea 0018.
     - Tasks with document links: expose as URL property to open the item. -->
