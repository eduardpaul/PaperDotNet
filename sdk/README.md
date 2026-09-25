# PaperDotNet SDKs (API-03)

Typed clients for C#, TypeScript and Python are generated with
[Kiota](https://github.com/microsoft/kiota) (MIT) from the OpenAPI description
[`openapi.json`](openapi.json). The generated code is committed and not edited
by hand. Design: [ADR-0021](../docs/adr/0021-mcp-and-sdks.md).

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

```ts
import { createPaperDotNetClient } from '@paperdotnet/client';
const api = createPaperDotNetClient({ baseUrl: 'https://dms.example.com', accessToken });
const page = await api.v10.workspaces.get();
```

```python
from paperdotnet_client import create_client
api = create_client("https://dms.example.com", access_token)
page = await api.v10.workspaces.get()
```

- **Authentication:** pass an API token or an OAuth access token. Pass
  `tenant` when the installation does not select the tenant by host name.
- **Errors:** API problems raise an `ApiException`, or the language's
  equivalent, with the status code.
- **Paging and ETags:** paged lists return `value` and `@odata.nextLink`. For
  `If-Match`, read the ETag from response headers, e.g. with a headers option
  in the request configuration.

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
   - TypeScript: `cd sdk/typescript && npm install && npm run build`;
   - Python: `pip install ./sdk/python`.
