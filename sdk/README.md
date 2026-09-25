# PaperDotNet SDKs (API-03)

Typed clients for C#, TypeScript and Python are generated with
[Kiota](https://github.com/microsoft/kiota) (MIT) from the OpenAPI description
[`openapi.json`](openapi.json). The generated code is committed and not edited
by hand. Design: [ADR-0021](../docs/adr/0021-mcp-and-sdks.md) and
[ADR-0032](../docs/adr/0032-sdk-for-first-party-clients.md).

| Language | Package | Location |
|---|---|---|
| C# (.NET 10) | `PaperDotNet.Client` | [`src/Sdk/PaperDotNet.Client`](../src/Sdk/PaperDotNet.Client) |
| TypeScript (ES modules, Node 18+ / browsers) | `@paperdotnet/client` | [`sdk/typescript`](typescript) |
| Python 3.9+ (asyncio) | `paperdotnet-client` | [`sdk/python`](python) |

## Usage

```csharp
var api = PaperDotNetClient.Create(new Uri("https://dms.example.com"), apiToken);
var workspace = await api.V10.Workspaces.PostAsync(new CreateWorkspaceRequest { Name = "Projects" });
var page = await api.V10.Workspaces.GetAsync();
```

```python
from paperdotnet_client import create_client
api = create_client("https://dms.example.com", access_token)
page = await api.v10.workspaces.get()
```

All SDKs rely on the same contract ([ADR-0032](../docs/adr/0032-sdk-for-first-party-clients.md)):
- **Errors:** every failed call throws the generated `ApiProblem`, with
  `responseStatusCode`, `code` and validation `errors`.
- **ETags:** resources carry `@odata.etag` (`odataEtag`). Send it back as
  `If-Match`. A stale one fails with 412.
- **Paging:** paged lists return `value` and `@odata.nextLink`.
- **Queries:** OData options (`filter`, `orderby`, `select`, `top`,
  `skiptoken`, `count`) are typed query parameters.

## TypeScript: building a frontend

The TypeScript package is what the web UI is built on. It contains the
generated client and models, plus a small runtime (`src/runtime`) for what
cannot be generated.

### Sign-in

```ts
import { OAuthSession, createPaperDotNetClient, webStorageStore } from '@paperdotnet/client';

const session = new OAuthSession({
  baseUrl: location.origin,
  redirectUri: `${location.origin}/callback`,     // listed in Auth:FirstPartyRedirectUris
  store: webStorageStore(sessionStorage),
});
const client = createPaperDotNetClient({ baseUrl: location.origin, auth: session });
const { api } = client;

// Sign-in page: create the session cookie, then run the authorization code flow with PKCE.
await api.v10.auth.login.post({ userName, password });   // passkeys: api.v10.auth.passkeys.*
location.href = (await session.beginSignIn()).href;
// Callback page:
await session.completeSignIn(location.href);
// Sign-out:
location.href = (await session.signOut(location.origin))?.href ?? '/';
```

- **Tokens:** they are refreshed before they expire. After a 401, the client
  refreshes once and repeats the call.
- **Scripts and tests:** use `session.signInWithPassword(user, password)`,
  or pass `accessToken` (an API token) instead of `auth`.
- **Tenant:** pass `tenant` when the installation does not select it by host
  name. It is sent as `X-Tenant`, which needs `Tenancy:AllowHeader`. Browser
  sign-in needs the host name to select the tenant.

### Items, queries and concurrency

```ts
import { fields, fieldsOf, ifMatch, all, isStatus, validationErrors } from '@paperdotnet/client';

const items = api.v10.workspaces.byWorkspaceId(ws).lists.byListId(list).items;
const item = await items.post({ fields: fields({ title: 'Invoice', amount: 120 }) });
fieldsOf(item).title;                                             // values as a plain object

for await (const entry of all(items, { queryParameters: { filter: "fields/amount gt 100", orderby: 'fields/due desc', top: 50 } })) { … }

try {
  await items.byItemId(item.id!).patch({ fields: fields({ amount: 130, note: null }) }, ifMatch(item));  // null removes a value
} catch (error) {
  if (isStatus(error, 412)) { /* changed by someone else: reload */ }
  const messages = validationErrors(error);                      // { 'fields.amount': ['…'] }
}
```

### Files

```ts
import { uploadBody, downloadFile } from '@paperdotnet/client';

const library = api.v10.workspaces.byWorkspaceId(ws).lists.byListId(lib);
const doc = await library.documents.post(await uploadBody({ file, title: 'Scan', languages: ['eng', 'deu'] }));
await api.v10.me.inbox.documents.post(await uploadBody({ file }));
await library.items.byItemId(id).file.put(await uploadBody({ file }));                 // new version
const bytes = await library.items.byItemId(id).file.get();                             // ArrayBuffer
const { blob, fileName } = await downloadFile(client, `/v1.0/workspaces/${ws}/lists/${lib}/items/${id}/file`);
const page1 = await library.items.byItemId(id).file.pages.byPage(1).image.get();     // JPEG
```

fetch cannot report upload progress, so show an indeterminate indicator.

### Live events and operations

```ts
import { subscribeLiveEvents, waitForOperation } from '@paperdotnet/client';

const subscription = subscribeLiveEvents(client, {
  'document.processing': (e) => refresh(e.itemId),       // status: scheduled, running, succeeded, failed
  notification: (n) => showToast(n.title),
  operation: (o) => progress(o.id, o.percentComplete),
  error: (e) => { if (e.closed) signIn(); },
});
subscription.close();

const done = await waitForOperation(client, operationId, { onProgress: (o) => progress(o.percentComplete) });
```

### Hosting the UI

- **Recommended:** serve it from the same origin as the API, or use a
  dev-server proxy for `/v1.0`, `/connect` and `/.well-known`.
- **Other origins:** list them in `Cors:Origins`, e.g.
  `PAPERDOTNET__Cors__Origins__0=http://localhost:5173`.

### End-to-end tests

```bash
cd sdk/typescript && npm install && npm run test:e2e
```

This builds and starts the server on a free port with a temporary SQLite
database, then runs `test/*.test.mjs` against the built package (about 30
seconds). To use a running server instead, set `PAPERDOTNET_URL` and
`PAPERDOTNET_ADMIN_PASSWORD`.

## Updating after API changes

1. Refresh the description:

   ```bash
   PAPERDOTNET_UPDATE_OPENAPI=1 dotnet test --solution PaperDotNet.slnx -- --filter-class "*OpenApiDocumentTests"
   ```

   Without the variable, that test fails when `openapi.json` is out of date.
2. Regenerate the clients: `sdk/generate.sh` (uses the `kiota` .NET tool from
   `.config/dotnet-tools.json`).
3. Check them:
   - C#: `dotnet build` (the integration tests use the C# client against the
     API);
   - TypeScript: `cd sdk/typescript && npm install && npm run build && npm run test:e2e`;
   - Python: `pip install ./sdk/python`.
