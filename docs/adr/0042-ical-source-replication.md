# ADR-0042: iCalendar source replication in the Calendar module

- **Status:** Accepted
- **Date:** 2026-10-02

## Context

[Idea 0024](../../ideas/0024-ical-url-replication.md) requires multiple inbound
calendar feeds per list and independent reuse of a URL across lists. The
existing Calendar file importer does not refresh URLs, isolate identities by
subscription, reconcile deletions or restore returning recycled events.

## Decision

Calendar owns subscriptions, encrypted URLs, bounded public HTTPS fetching,
snapshot validation, source identities, reconciliation, status, API endpoints,
a workflow activity and its scheduled built-in workflow. It references only
the existing extension SDK, with no Google-specific dependency or required
external service. Lists remains the generic item engine.

Use the existing workflow engine for automatic refresh and operations for
initial/manual refresh. Each source has a database lease for refresh and
configuration changes. Identity is saved before a cross-module item create,
so a retry finds the same item. Deletions run only after snapshot validation
and successful upserts. There is no transaction spanning module databases;
a mid-apply failure can expose earlier updates, but leaves absence-based
removal unstarted and can be retried.

Source fields and recurrence are read-only while subscribed. Local metadata
and local events remain independent. Source removal detaches retained items.
Feed parsing uses Ical.Net; unsupported or incomplete snapshots are rejected
rather than treating unrecognized events as removed. Feed date-window limits
are inherited from the publisher.

The SDK remains at version 1.0 until the first release. This change adds two
generic capabilities:

- `IListItemStore.RestoreAsync`: tenant- and permission-checked, idempotent
  restoration through Lists' existing writer, events and search pipeline.
- `IWorkflowDirectory.EnableBuiltInAsync`: activation of registered workspace
  built-ins using the existing validator/writer. Trusted module callers must
  authorize configuration first; arbitrary workflow JSON is not accepted.

Default interface implementations keep older custom implementations compatible
(they report unsupported/false); the host supplies both capabilities. Calendar
uses these contracts without referencing another module's implementation.
No other extension-model change is required.

## Consequences

Google secret iCal URLs and other compatible feeds share one implementation.
No Google OAuth grant, owner relationship or outbound sync/conflict model is
needed. Two feeds with the same UID remain independent. Private addresses,
redirect downgrades and oversized responses are refused; URL secrets are
redacted and excluded from portable templates. SQLite and PostgreSQL carry
the same source state and concurrency rules.
