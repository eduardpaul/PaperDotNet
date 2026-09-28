# 0011: Item change events reach every user of the tenant

- **Status:** possible
- **Area:** API
- **Date:** 2026-09-28

## Problem

`ItemWriter` publishes `item.changed` live events without a user, so
`/v1.0/me/events` delivers them to every connected user of the tenant. The
payload has the workspace, list and item ids, including items and lists the
user cannot read. It is ids only, but it tells people that something changed
in a list they cannot see, and every client receives every change of the
tenant.

## Where it shows up

- `ItemWriter.PublishChanged` in
  [`ItemWriter`](../src/Modules/Lists/PaperDotNet.Lists/Features/ItemWriter.cs)
- [`LiveEvent`](../src/BuildingBlocks/PaperDotNet.Abstractions/LiveEvents.cs):
  a null user means every user of the tenant.

## Possible approaches

- Send the event to the readers of the item's scope: match the scope's ACL
  entries against the principal sets of the connected users (kept in memory
  per connection) ([item-and-permission-storage.md](../docs/item-and-permission-storage.md),
  decision 3).
