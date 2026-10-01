using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Extensions;

/// <summary>Version of the extension SDK implemented by this host (major.minor).</summary>
public static class ExtensionSdk
{
    public static readonly Version Version = new(1, 0);

    /// <summary>Logical name of the embedded manifest resource.</summary>
    public const string ManifestResource = "paperdotnet.extension.json";
}

/// <summary>
/// Marks an assembly as a PaperDotNet extension. The host's source generator finds referenced extensions at compile
/// time (ADR-0014): no runtime scanning.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PaperDotNetExtensionAttribute(Type extensionType) : Attribute
{
    public Type ExtensionType { get; } = extensionType;
}

/// <summary>
/// An extension. <see cref="Configure"/> runs once at startup (before any tenant is known) and registers
/// contributions; each one is active only in tenants that enabled the extension. Needs a public parameterless
/// constructor. Under Native AOT (ADR-0039) an extension follows the same rules as a module: its JSON is
/// source-generated (<see cref="IExtensionBuilder.AddJson"/>) and its endpoints use the request delegate generator.
/// </summary>
public interface IExtension
{
    void Configure(IExtensionBuilder builder);
}

/// <summary>
/// Receives an integration event in the background, in tenants that enabled the extension. Must be idempotent: an
/// event can be delivered again. The event carries its tenant (<see cref="IntegrationEvent.TenantId"/>).
/// </summary>
public interface IEventSubscriber<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>Registers an extension's contributions (extension points, EXT-04).</summary>
public interface IExtensionBuilder
{
    ExtensionManifest Manifest { get; }

    /// <summary>The host's services: register the extension's own services here.</summary>
    IServiceCollection Services { get; }

    IConfiguration Configuration { get; }

    /// <summary>The extension's source-generated JSON metadata (the types of its endpoints and settings).</summary>
    IExtensionBuilder AddJson(IJsonTypeInfoResolver json);

    /// <summary>A field type (name must start with <c>{extension id}.</c>).</summary>
    IExtensionBuilder AddFieldType(IFieldType fieldType);

    /// <summary>An item mutator (runs inside item writes, before they are saved), filtered by <see cref="ItemMutatorOptions"/> (EVT-03).</summary>
    IExtensionBuilder AddItemMutator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMutator>(Action<ItemMutatorOptions>? configure = null)
        where TMutator : class, IItemMutator;

    /// <summary>
    /// A subscriber to a list event: <see cref="ItemAdded"/>, <see cref="ItemUpdated"/>, <see cref="ItemDeleted"/>,
    /// <see cref="ItemRestored"/>, <see cref="ItemPurged"/>, <see cref="ListCreated"/> or <see cref="ListDeleted"/>.
    /// </summary>
    IExtensionBuilder AddEventSubscriber<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSubscriber>()
        where TEvent : IntegrationEvent
        where TSubscriber : class, IEventSubscriber<TEvent>;

    /// <summary>A job on a cron schedule (UTC), run in every tenant that enabled the extension (name starts with <c>{extension id}.</c>).</summary>
    IExtensionBuilder AddRecurringJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(string name, string cronSchedule)
        where TJob : class, ITenantRecurringJob;

    /// <summary>
    /// A content type managed by the extension (key starts with <c>{extension id}.</c>). It is provisioned into a tenant
    /// when the tenant enables the extension, and kept in sync.
    /// </summary>
    IExtensionBuilder AddContentType(ContentTypeTemplate contentType);

    /// <summary>
    /// A term set (TAX-11; key starts with <c>{extension id}.</c>), provisioned into a tenant when it enables the
    /// extension: missing terms and synonyms are added, terms are never removed.
    /// </summary>
    IExtensionBuilder AddTermSet(Taxonomy.Contracts.TermSetTemplate termSet);

    /// <summary>A list template (LST-16; key starts with <c>{extension id}.</c>), offered where the extension is enabled.</summary>
    IExtensionBuilder AddListTemplate(ListTemplateDefinition listTemplate);

    /// <summary>
    /// A section of provisioning templates (PRV-05) in the extension's own XML namespace: exported where the extension is
    /// enabled, applied where it is enabled or the same template enables it.
    /// </summary>
    IExtensionBuilder AddTemplateSection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, Provisioning.Contracts.ITemplateHandler;

    /// <summary>
    /// A workflow trigger the extension raises with <c>IWorkflowTriggers</c> (EVT-09; key starts with <c>{extension id}.</c>),
    /// offered in the trigger catalog.
    /// </summary>
    IExtensionBuilder AddWorkflowTrigger(WorkflowTriggerDefinition trigger);

    /// <summary>
    /// A built-in workflow the extension ships (EVT-12; key starts with <c>{extension id}.</c>), offered in workspaces where
    /// the extension is enabled.
    /// </summary>
    IExtensionBuilder AddWorkflow(BuiltInWorkflow workflow);

    /// <summary>An activity for workflows (EVT-09; key starts with <c>{extension id}.</c>).</summary>
    IExtensionBuilder AddWorkflowActivity<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TActivity>()
        where TActivity : class, IWorkflowActivity;

    /// <summary>
    /// A tool for AI assistants on the MCP endpoint (API-09). The name must start with the extension id
    /// with <c>.</c> and <c>-</c> replaced by <c>_</c>, then <c>_</c> (e.g. <c>acme_invoices_approve</c>);
    /// the tool is offered only in tenants that enabled the extension.
    /// </summary>
    IExtensionBuilder AddMcpTool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTool>()
        where TTool : class, Mcp.Contracts.IMcpTool;

    /// <summary>
    /// The extension's own tables (EXT-07): a scoped DbContext on the server's database, with the SQL of its migrations
    /// embedded in the extension assembly (see <see cref="ExtensionDbContext"/>).
    /// </summary>
    IExtensionBuilder AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>()
        where TContext : ExtensionDbContext;

    /// <summary>API endpoints under <c>/v1.0/ext/{id}</c> (404 in tenants where the extension is disabled).</summary>
    IExtensionBuilder MapEndpoints(Action<IEndpointRouteBuilder> map);
}

/// <summary>Where an item mutator runs (EVT-03). Empty lists mean "any".</summary>
public sealed class ItemMutatorOptions
{
    /// <summary>Lower runs first (built-in default 1000).</summary>
    public int Sequence { get; set; } = 1000;

    /// <summary>
    /// Keys of content types (e.g. the extension's own <c>acme.invoices.invoice</c>, which keeps its key when a tenant
    /// already has a content type of the same name) or content type names (e.g. <c>Invoice</c>).
    /// </summary>
    public List<string> ContentTypes { get; } = [];

    /// <summary>List names.</summary>
    public List<string> Lists { get; } = [];

    /// <summary>Keys of the list templates the list was created from (e.g. <c>tasks</c>).</summary>
    public List<string> ListTemplates { get; } = [];

    public bool IncludeFolders { get; set; }

    /// <summary>Additional condition on the scope.</summary>
    public Func<ItemEventScope, bool>? Condition { get; set; }
}

/// <summary>Per-tenant state of extensions (enabled, settings). The tenant is explicit: there is no ambient tenant (ADR-0039).</summary>
public interface IExtensionState
{
    ValueTask<bool> IsEnabledAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken);

    /// <summary>The extension's settings in the tenant, with manifest defaults applied.</summary>
    ValueTask<JsonObject> GetSettingsAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken);
}
