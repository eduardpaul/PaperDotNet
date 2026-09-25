# Comments, activity and the sync API

Phase 5d: people discuss items where they live (LST-17), and API clients stay
in sync cheaply (API-04…06). Design: [ADR-0020](adr/0020-sync-api.md).

## Comments and activity (LST-17)

Every list item (documents, tasks, events, any list) has comments and an
activity timeline. Access follows the item: whoever can read it sees its
comments and activity; commenting needs Contribute.

| Request | What it does |
|---|---|
| `GET …/items/{id}/comments` | Comments, oldest first (`$top`, `$skiptoken`) |
| `POST …/items/{id}/comments` | `{ "text", "parentId"?, "mentions"?: [userId] }` |
| `GET …/items/{id}/comments/{commentId}` | One comment, with its ETag |
| `PATCH …/items/{id}/comments/{commentId}` | Authors change `text` and `mentions` (`If-Match`) |
| `DELETE …/items/{id}/comments/{commentId}` | The author or someone with Manage; replies go too |
| `GET …/items/{id}/activity` | The timeline, newest first |

- **Replies** point to a top-level comment (one level, as in most chat UIs).
- **Mentions** are user ids (a UI resolves `@name` to ids). Mentioned users get
  a `mention` notification, but only if they can read the item. Adding people
  in an edit notifies only them.
- **Search:** comment text is part of the item's search document.
- **Timeline:** `created`, `updated` (with `changedFields`), `deleted`,
  `restored`, `commented`, `approval` (workflow decisions) and entries of
  extensions (`IItemActivity`, see [extensions.md](extensions.md)). Each entry
  has the actor and the time of the change.
- Permanently deleting an item removes its comments and timeline.
- Scopes: `comment.read`, `comment.write`.

## Delta sync (API-05)

`GET /v1.0/workspaces/{ws}/lists/{list}/items/delta` returns what changed in a
list since the last call. Works for every list, including document libraries.

1. **First call** (no token): all items the caller can read, in pages
   (`$top`, default 200, max 1000). Follow `@odata.nextLink`; the last page has
   an `@odata.deltaLink`.
2. **Later calls:** call the `deltaLink`. The answer has only the changes since
   then, each item once with its current state, and a new `deltaLink`.
   - Changed, added or restored items come back in full, like in the items API.
   - Deleted items (recycle bin or purged) come back as
     `{ "id": "…", "@removed": { "reason": "deleted" } }`.
3. **410 `resyncRequired`:** start again without a token. This happens when:
   - permissions in the list changed (inheritance broken or reset, grants
     changed, an item moved to another security scope);
   - the token is older than the change log (`Lists:DeltaRetentionDays`,
     default 30).

Changes to items the caller cannot read are left out, deletions too. Changes
to **workspace membership** do not reset tokens. After a user loses access to a
workspace, the list is not visible any more (404). After a user gains access,
start a new sync.

Tokens are opaque. A client stores the latest `deltaLink` after it has applied
the page. Changes become visible to delta after a short safety window
(`Lists:DeltaSafetyWindow`, default 2 s), so transactions that commit late are
not skipped.

## Change notifications (API-06)

API clients subscribe to changes and get signed HTTP POSTs.

```http
POST /v1.0/changeSubscriptions
{
  "resource": "workspaces/{ws}/lists/{list}/items",      // or …/items/{id}
  "changeTypes": ["created", "updated", "deleted"],
  "notificationUrl": "https://example.com/hooks/paperdotnet",
  "clientState": "my-own-check-value",
  "expirationDateTime": "2026-10-20T00:00:00Z"            // max 30 days, default the max
}
```

- **Validation:** before the subscription is created, PaperDotNet posts to
  `notificationUrl?validationToken=…`. The receiver must answer 200 with the
  token as a plain-text body.
- **Secret:** the response contains a `secret` (shown only once).
- **Notifications** carry ids, not data. The client reads the item with its
  own token.

  ```json
  { "value": [ {
      "subscriptionId": "…", "clientState": "my-own-check-value", "changeType": "updated",
      "resource": "workspaces/…/lists/…/items/…",
      "resourceData": { "id": "…", "workspaceId": "…", "listId": "…" },
      "occurredAt": "…", "subscriptionExpirationDateTime": "…", "tenant": "acme" } ] }
  ```

  - Headers as for user webhooks: `X-PaperDotNet-Event: change`,
    `X-PaperDotNet-Delivery`, `X-PaperDotNet-Timestamp`, and
    `X-PaperDotNet-Signature: sha256=HMAC(secret, "{timestamp}.{body}")`.
  - Delivery is at least once. Deduplicate by `X-PaperDotNet-Delivery`.
  - Failures are retried with backoff (1 min … 12 h, six attempts).
- **Only readable changes:** a notification is sent only if the subscription's
  owner can read the item (for deletions: the list).
- **Network rules** as for user webhooks: https only
  (`Notifications:AllowHttpWebhooks`), no private addresses
  (`Notifications:AllowPrivateNetworkWebhooks`).
- **Managing subscriptions:**
  - Renew with `PATCH /v1.0/changeSubscriptions/{id}`
    `{ "expirationDateTime" }` and `If-Match`.
  - Delete with `DELETE`, list with `GET /v1.0/changeSubscriptions`.
  - Up to 100 per user.
  - Expired subscriptions are removed after a week.
- Scope: `changeSubscription.manage`.

For catching up after downtime, combine both: a notification tells the client
to call its `deltaLink`.

## Batching (API-04)

`POST /v1.0/$batch` runs up to 20 requests in one round trip:

```json
{ "requests": [
  { "id": "1", "method": "POST", "url": "/workspaces/…/lists/…/items", "body": { "fields": { "title": "A" } } },
  { "id": "2", "method": "GET", "url": "/workspaces/…/lists/…/items?$top=5", "dependsOn": ["1"] },
  { "id": "3", "method": "PATCH", "url": "/workspaces/…/lists/…/items/…", "headers": { "If-Match": "\"3\"" }, "body": { "fields": { "done": true } } }
] }
```

- **URLs** are relative to `/v1.0` (or start with it). Batches cannot be nested.
- **Execution:** each request runs like a normal one, with the caller's token,
  tenant, scopes and permissions.
- **Order:** requests run in the order given.
- **`dependsOn`** names earlier requests. If one of them failed, the request is
  not run and gets 424.
- **Response:**
  `{ "responses": [ { "id", "status", "headers", "body" } ] }`.
  - `headers` keeps `Content-Type`, `ETag`, `Location` and `Retry-After`.
  - JSON bodies stay JSON; text becomes a string; binary becomes base64.
- **No transaction:** requests do not share one. A failed request does not undo
  earlier ones.
