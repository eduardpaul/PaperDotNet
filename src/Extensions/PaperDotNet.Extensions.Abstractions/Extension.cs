using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Extensions;

/// <summary>Version of the extension SDK implemented by this host (major.minor).</summary>
public static class ExtensionSdk
{
    public static readonly Version Version = new(1, 0);

    /// <summary>Logical name of the embedded manifest resource.</summary>
    public const string ManifestResource = "paperdotnet.extension.json";
}

/// <summary>
/// Marks an assembly as a PaperDotNet extension. The host's source generator finds
/// referenced extensions at compile time (ADR-0014): no runtime scanning.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PaperDotNetExtensionAttribute(Type extensionType) : Attribute
{
    public Type ExtensionType { get; } = extensionType;
}

/// <summary>
/// An extension. <see cref="Configure"/> runs once at startup (before the tenant is
/// known) and registers contributions; each one is active only in tenants that enabled
/// the extension. Needs a public parameterless constructor.
/// </summary>
public interface IExtension
{
    void Configure(IExtensionBuilder builder);
}

/// <summary>Registers an extension's contributions (extension points, EXT-04).</summary>
public interface IExtensionBuilder
{
    ExtensionManifest Manifest { get; }

    /// <summary>The host's services: register the extension's own services here.</summary>
    IServiceCollection Services { get; }

    IConfiguration Configuration { get; }

    /// <summary>A field type (name must start with <c>{extension id}.</c>).</summary>
    IExtensionBuilder AddFieldType(IFieldType fieldType);

    /// <summary>A synchronous before/after item receiver, filtered by <see cref="ItemReceiverOptions"/> (EVT-03).</summary>
    IExtensionBuilder AddItemReceiver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReceiver>(Action<ItemReceiverOptions>? configure = null)
        where TReceiver : class, IItemEventReceiver;

    /// <summary>An asynchronous subscriber to an integration event (e.g. <c>ItemAdded</c>).</summary>
    IExtensionBuilder AddEventSubscriber<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSubscriber>()
        where TEvent : IntegrationEvent
        where TSubscriber : class, IEventSubscriber<TEvent>;

    /// <summary>An integration event type defined by the extension (so it can be published and subscribed to).</summary>
    IExtensionBuilder AddIntegrationEvent<TEvent>()
        where TEvent : IntegrationEvent;

    /// <summary>A job on a cron schedule (UTC), run in every tenant that enabled the extension.</summary>
    IExtensionBuilder AddRecurringJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(string name, string cronSchedule)
        where TJob : class, ITenantRecurringJob;

    /// <summary>
    /// A content type managed by the extension (key starts with <c>{extension id}.</c>). It is
    /// provisioned into a tenant when the tenant enables the extension, and kept in sync.
    /// </summary>
    IExtensionBuilder AddContentType(ContentTypeTemplate contentType);

    /// <summary>A list template (LST-16; key starts with <c>{extension id}.</c>), offered where the extension is enabled.</summary>
    IExtensionBuilder AddListTemplate(ListTemplateDefinition listTemplate);

    /// <summary>
    /// The extension's own tables (EXT-07, one context per extension) in the schema
    /// <c>ext_{id}</c>. Migrations come from <c>{assembly of TContext}.Migrations.{Sqlite|PostgreSql}</c>
    /// and run at startup with the host's; data stays when a tenant disables the extension.
    /// </summary>
    IExtensionBuilder AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>()
        where TContext : ExtensionDbContext;

    /// <summary>
    /// A provisioning template section (PRV-05) in the extension's own XML namespace: exported and
    /// applied with the rest of a template in tenants that enabled the extension.
    /// </summary>
    IExtensionBuilder AddTemplateHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, ITemplateHandler;

    /// <summary>An action for rules and workflows (EVT-09; key starts with <c>{extension id}.</c>).</summary>
    IExtensionBuilder AddAutomationAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAction>()
        where TAction : class, IAutomationAction;

    /// <summary>A trigger for rules (EVT-09; key starts with <c>{extension id}.</c>); raise it with <see cref="IAutomationTriggers"/>.</summary>
    IExtensionBuilder AddAutomationTrigger(AutomationTriggerDefinition trigger);

    /// <summary>
    /// A tool for AI assistants on the MCP endpoint (API-09). The name must start with the extension id
    /// with <c>.</c> and <c>-</c> replaced by <c>_</c>, then <c>_</c> (e.g. <c>acme_invoices_approve</c>);
    /// the tool is offered only in tenants that enabled the extension.
    /// </summary>
    IExtensionBuilder AddMcpTool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTool>()
        where TTool : class, Mcp.Contracts.IMcpTool;

    /// <summary>API endpoints under <c>/v1.0/extensions/{id}</c> (404 in tenants where the extension is disabled).</summary>
    IExtensionBuilder MapEndpoints(Action<IEndpointRouteBuilder> map);
}

/// <summary>Where an item receiver runs (EVT-03). Empty lists mean "any".</summary>
public sealed class ItemReceiverOptions
{
    /// <summary>Lower runs first (built-in default 1000).</summary>
    public int Sequence { get; set; } = 1000;

    /// <summary>Content type names, e.g. <c>Invoice</c>.</summary>
    public List<string> ContentTypes { get; } = [];

    /// <summary>List names.</summary>
    public List<string> Lists { get; } = [];

    /// <summary>Keys of the list templates the list was created from (e.g. <c>tasks</c>).</summary>
    public List<string> ListTemplates { get; } = [];

    public bool IncludeFolders { get; set; }

    /// <summary>Additional condition on the scope.</summary>
    public Func<ItemEventScope, bool>? Condition { get; set; }
}

/// <summary>Per-tenant state of extensions (enabled, settings).</summary>
public interface IExtensionState
{
    ValueTask<bool> IsEnabledAsync(string extensionId, CancellationToken cancellationToken);

    /// <summary>The extension's settings in the current tenant, with manifest defaults applied.</summary>
    ValueTask<JsonObject> GetSettingsAsync(string extensionId, CancellationToken cancellationToken);
}
