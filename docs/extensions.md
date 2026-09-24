# Writing extensions

PaperDotNet extensions are .NET class libraries **compiled into the host**
(ADR-0014): no runtime plugin loading, so every build type works (including a
future Native AOT host). Operators install extension code; tenant admins
enable, configure and disable it for their organization.

Sample: [`samples/PaperDotNet.Samples.Invoices`](../samples/PaperDotNet.Samples.Invoices).

## 1. Project

Reference only the SDK, `PaperDotNet.Extensions.Abstractions`, and embed the
manifest:

```xml
<ItemGroup>
  <PackageReference Include="PaperDotNet.Extensions.Abstractions" Version="1.0.*" />
  <EmbeddedResource Include="extension.json" LogicalName="paperdotnet.extension.json" />
</ItemGroup>
```

## 2. Manifest (`extension.json`)

Language-neutral, validated at startup; schema:
[`docs/schemas/extension-manifest.schema.json`](schemas/extension-manifest.schema.json).

- `id`: reverse-DNS (`acme.invoices`). It prefixes your scopes, field types,
  job names and routes (`/v1.0/ext/{id}/…`).
- `sdkVersion`: SDK `major.minor` you built against; the host accepts the same
  major and an equal or older minor.
- `scopes`: permissions you add (IAM-13). `grantedToMembers` scopes are added
  to the Member role when a tenant enables the extension.
- `settings`: typed tenant settings (`text`, `number`, `boolean`, `choice`)
  with defaults; tenants edit them at `PUT /v1.0/extensions/{id}/settings`.
- `autoEnable`: enabled in tenants that have not decided otherwise.

## 3. Code

```csharp
[assembly: PaperDotNetExtension(typeof(InvoicesExtension))]

public sealed class InvoicesExtension : IExtension
{
    public void Configure(IExtensionBuilder builder)
    {
        builder.Services.AddSingleton<MyService>();              // your own services
        builder.AddFieldType(new IbanFieldType());                // "acme.invoices.iban"
        builder.AddItemReceiver<ApprovalReceiver>(o =>            // before/after handlers (EVT-03)
        {
            o.Sequence = 100;
            o.ContentTypes.Add("Invoice");
        });
        builder.AddEventSubscriber<ItemAdded, InvoiceCounter>(); // async, at-least-once
        builder.AddRecurringJob<ReminderJob>("acme.invoices.reminders", "0 7 * * *");
        builder.MapEndpoints(api => api.MapGet("/stats", ...).RequireScope("acme.invoices.read"));
    }
}
```

Every contribution is active only in tenants that enabled the extension:
endpoints answer 404 `extensionDisabled`, receivers, subscribers and jobs
skip, and the field types cannot be used in new content types. Read the
tenant's settings with `IExtensionState.GetSettingsAsync(id)`.

## 4. Add it to a host build

Reference the package (or project) from `src/PaperDotNet.Host` (or your own
host project that references the PaperDotNet host) and rebuild the image. The
host's source generator finds every referenced assembly with
`[assembly: PaperDotNetExtension]` at compile time; an invalid extension type
is a build error (`PDN0001`), an invalid manifest stops startup with a clear
message. `GET /v1.0/extensions` lists what the build contains.

Tests and custom hosts can also add instances to
`PaperDotNetHost.AdditionalExtensions` before the host is built.
