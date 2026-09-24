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
        builder.AddContentType(new ContentTypeTemplate("acme.invoices.invoice", "Invoice", null, [...fields]));
        builder.AddListTemplate(new ListTemplateDefinition("acme.invoices.invoices", "Invoices", null,
            ["acme.invoices.invoice"], [new ViewTemplate("All invoices", ["title", "amount"])]));
    }
}
```

Content types you add are provisioned into a tenant when it enables the
extension (a name clash gets a suffix, e.g. `Invoice (2)`), stay in sync with
your definition, and cannot be changed by tenants. List templates appear in
`GET /v1.0/listTemplates` where the extension is enabled. Receivers can be
limited to lists created from a template (`o.ListTemplates.Add(...)`). Mark
how fields count in search with `FieldDefinition.Search` (`None`, `Normal`,
`High`; SRC-06).

Every contribution is active only in tenants that enabled the extension:
endpoints answer 404 `extensionDisabled`, receivers, subscribers and jobs
skip, and the field types cannot be used in new content types. Read the
tenant's settings with `IExtensionState.GetSettingsAsync(id)`.

## 4. Data (EXT-07)

Two ways to keep data, both inside the tenant's isolation boundary:

**List items** — for data people should see, edit, search and version like any
other content. Inject `IListItemStore` (Lists.Contracts): it reads and writes
items through the same pipeline as the API (field validation, receivers,
versions, events, search). It acts as the current user with their
permissions; `AsSystem()` gives full control over the tenant's lists for jobs
and other background work. Filters use the items API's OData syntax.

```csharp
var result = await items.UpdateAsync(ws, list, itemId, new JsonObject { ["status"] = "approved" }, item.Version, ct);
if (result.Status == ListItemStatus.VersionMismatch) { /* someone else changed it */ }

var system = items.AsSystem();
foreach (var l in await system.GetListsAsync(null, "acme.invoices.invoices", ct))
{
    var (pending, _) = await system.QueryAsync(l.WorkspaceId, l.Id, new ListItemQuery("fields/status eq 'pendingApproval'"), ct);
}
```

`IListItemStore` also gives `GetListAsync` (with the caller's access and
whether the list is a library), `EnsureHomeAsync` (the caller's Home workspace
and Inbox) and the caller's access on every item (`ListItemData.Access`).
`ItemPurged` tells subscribers that an item was deleted permanently.
Receivers can match content types by template key (`ItemEventScope.ContentTypeKey`,
e.g. `event`). `ITenantScopeFactory.CreateScopeAsync(tenantId, userId)` opens a
scope for an active tenant known only by id (e.g. from a token).
Implement `IItemSearchContributor` to add text (and its language) to items'
search documents, and call `IListItemStore.ReindexAsync(itemId)` when it
changes. Push notifications to connected clients with `ILiveEvents.Publish`
(they arrive on `GET /v1.0/me/events`). Notify users with
`INotificationSender.SendAsync(message, userIds)`: the message lands in their
inbox and goes to their channels (webhook) according to their preferences;
pass a `DeduplicationKey` so a job that runs again does not notify twice.

**Files** — `IBlobStore` (PaperDotNet.Abstractions) stores binary content on
the installation's storage (local disk by default). Choose keys under the
tenant id, e.g. `{tenantId:N}/…`.

**Own tables** — for technical or high-volume data. Derive from
`ExtensionDbContext`, configure entities in `ConfigureModel` and register it
with `builder.AddDbContext<TContext>()` (one per extension). The tables go to
the schema `ext_{id}` (dots and dashes become `_`, e.g. `ext_acme_invoices`;
on SQLite a table-name prefix). Every entity must implement `ITenantOwned`: the
tenant query filter, PostgreSQL row-level security and the audit log
(`/v1.0/auditLog`, entity type `{context name}.{Type}`) apply as for modules;
`ISoftDeletable`, `IVersioned` and `IAuditable` work too. Data stays when a
tenant disables the extension.

```csharp
public sealed class InvoicesDbContext(DbContextOptions<InvoicesDbContext> options, ITenantContext tenant)
    : ExtensionDbContext(options, tenant)
{
    public DbSet<ApprovalRecord> Approvals => Set<ApprovalRecord>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ApprovalRecord>(b => b.ToTable("approvals"));
}
```

Migrations live in two companion projects named
`{extension assembly}.Migrations.Sqlite` and `.PostgreSql`; they reference the
extension and `PaperDotNet.Persistence.Sqlite` / `.PostgreSql`, contain an
`IDesignTimeDbContextFactory` (see `samples/PaperDotNet.Samples.Invoices.Migrations.*`)
and are generated with

```bash
dotnet ef migrations add <Name> -p samples/PaperDotNet.Samples.Invoices.Migrations.Sqlite -c InvoicesDbContext -o Generated
dotnet ef migrations add <Name> -p samples/PaperDotNet.Samples.Invoices.Migrations.PostgreSql -c InvoicesDbContext -o Generated
```

The host runs them at startup with its own migrations. The extension project
itself never references a database provider.

## 5. Analyzers

The SDK package brings analyzers that turn the platform rules into build
diagnostics (warnings are errors if your project treats them so):

| Rule | Severity | What |
|------|----------|------|
| PDN1001 | Error | An entity of an `ExtensionDbContext` does not implement `ITenantOwned` |
| PDN1002 | Error | `IgnoreQueryFilters()` without filter names, or naming the `Tenant` filter |
| PDN1003 | Error | Raw SQL (`FromSqlRaw`, `ExecuteSql…`, `SqlQuery…`): use LINQ so both providers and the tenant filter work |
| PDN1004 | Warning | A class implements `IExtension` but the assembly has no `[assembly: PaperDotNetExtension(typeof(…))]` |
| PDN1005 | Warning | `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`: use `TimeProvider` |
| PDN1006 | Warning | `Guid.NewGuid()`: use `Ids.New()` (UUIDv7) |

## 6. Testing

`PaperDotNet.Extensions.Testing` runs the real host in-process with your
extension (migrations, authentication, the extension runtime), on a temporary
SQLite database by default. It does not depend on a test framework; with
xUnit v3 share one host per run and give every test its own tenant:

```csharp
[assembly: AssemblyFixture(typeof(InvoicesHost))]

public sealed class InvoicesHost() : ExtensionTestHost(new InvoicesExtension());

public sealed class InvoicesExtensionTests(InvoicesHost host)
{
    [Fact]
    public async Task Large_invoices_wait_for_approval()
    {
        var tenant = await host.CreateTenantAsync();            // admin + extension enabled
        using var admin = await tenant.CreateClientAsync();     // signed-in API client
        await tenant.ConfigureAsync("acme.invoices", new { approvalThreshold = 1000 });
        var item = await tenant.RunAsync(sp => sp.GetRequiredService<IListItemStore>().CreateAsync(...));
    }
}
```

`CreateUserAsync(name)` adds a member and returns their client;
`RunAsync` runs code inside the tenant as the administrator (or another user).
For PostgreSQL pass `new ExtensionTestHostOptions { DatabaseProvider = "PostgreSql",
ConnectionString = ... }`. Reference your migrations projects from the test
project. See `samples/PaperDotNet.Samples.Invoices.Tests`.

## 7. Add it to a host build

Reference the package (or project) from `src/PaperDotNet.Host` (or your own
host project that references the PaperDotNet host) and rebuild the image. The
host's source generator finds every referenced assembly with
`[assembly: PaperDotNetExtension]` at compile time; an invalid extension type
is a build error (`PDN0001`), an invalid manifest stops startup with a clear
message. `GET /v1.0/extensions` lists what the build contains. An extension
with its own tables also needs its migrations projects referenced; without
them startup stops with a message naming the missing assembly.

Tests and custom hosts can also add instances to
`PaperDotNetHost.AdditionalExtensions` before the host is built.
