using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Extensions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.ExtensionHost.Runtime;

/// <summary>What an extension contributes (for the catalog API and validation).</summary>
public sealed class ExtensionContributions
{
    public List<string> FieldTypes { get; } = [];

    public List<string> ItemReceivers { get; } = [];

    public List<string> EventSubscribers { get; } = [];

    public List<string> Jobs { get; } = [];

    public List<string> ContentTypes { get; } = [];

    public List<string> ListTemplates { get; } = [];

    public List<string> TemplateHandlers { get; } = [];

    public List<string> AutomationActions { get; } = [];

    public List<string> AutomationTriggers { get; } = [];

    /// <summary>The extension's own DbContext (EXT-07), if any.</summary>
    public string? DbContext { get; set; }

    public bool Endpoints { get; set; }
}

/// <summary>A compiled-in extension with its validated manifest.</summary>
public sealed class LoadedExtension(IExtension extension, ExtensionManifest manifest)
{
    public IExtension Instance { get; } = extension;

    public ExtensionManifest Manifest { get; } = manifest;

    public string Id => Manifest.Id;

    public ExtensionContributions Contributions { get; } = new();

    internal List<Action<IEndpointRouteBuilder>> EndpointMaps { get; } = [];
}

/// <summary>All extensions of this build (frozen at startup).</summary>
public sealed class ExtensionCatalog(IReadOnlyList<LoadedExtension> extensions)
{
    private readonly FrozenDictionary<string, LoadedExtension> _byId = extensions.ToFrozenDictionary(e => e.Id, StringComparer.Ordinal);
    private readonly FrozenDictionary<string, string> _fieldTypeOwners = extensions
        .SelectMany(e => e.Contributions.FieldTypes.Select(t => (Type: t, e.Id)))
        .ToFrozenDictionary(x => x.Type, x => x.Id, StringComparer.Ordinal);

    public IReadOnlyList<LoadedExtension> All { get; } = extensions;

    public LoadedExtension? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The extension that contributed a field type, or null for built-in types.</summary>
    public string? FieldTypeOwner(string fieldType) => _fieldTypeOwners.GetValueOrDefault(fieldType);
}

public static class ExtensionRegistration
{
    public const string RoutePrefix = "ext";

    /// <summary>
    /// Validates the manifests of <paramref name="extensions"/> and lets each one register its
    /// contributions. Fails fast at startup on invalid or conflicting extensions.
    /// </summary>
    public static IServiceCollection AddPaperDotNetExtensions(this IServiceCollection services, IConfiguration configuration, IEnumerable<IExtension> extensions)
    {
        var loaded = new List<LoadedExtension>();
        foreach (var extension in extensions)
        {
            var manifest = ReadManifest(extension);
            var errors = manifest.Validate(ExtensionSdk.Version);
            if (loaded.Any(l => l.Id == manifest.Id))
            {
                errors = [.. errors, $"Another extension already uses the id '{manifest.Id}'."];
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Extension {extension.GetType().FullName} is invalid: {string.Join(" ", errors)}");
            }

            var entry = new LoadedExtension(extension, manifest);
            extension.Configure(new ExtensionBuilder(entry, services, configuration));
            services.AddScopes([.. manifest.Scopes.Select(s => new ScopeDefinition(s.Name, s.Description, s.GrantedToMembers))]);
            loaded.Add(entry);
        }

        services.AddSingleton(new ExtensionCatalog(loaded));
        return services;
    }

    /// <summary>Maps extension endpoints under <c>/v1.0/ext/{id}</c>, answering 404 where the extension is disabled.</summary>
    public static IEndpointRouteBuilder MapPaperDotNetExtensions(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.ServiceProvider.GetRequiredService<ExtensionCatalog>();
        foreach (var extension in catalog.All.Where(e => e.EndpointMaps.Count > 0))
        {
            var id = extension.Id;
            var group = endpoints.MapV1Group($"{RoutePrefix}/{id}", $"Extension: {extension.Manifest.Name}");
            group.AddEndpointFilter(async (context, next) =>
                await context.HttpContext.RequestServices.GetRequiredService<IExtensionState>().IsEnabledAsync(id, context.HttpContext.RequestAborted)
                    ? await next(context)
                    : ApiErrors.Problem(StatusCodes.Status404NotFound, "extensionDisabled", "The extension is not enabled for this organization."));
            foreach (var map in extension.EndpointMaps)
            {
                map(group);
            }
        }

        return endpoints;
    }

    private static ExtensionManifest ReadManifest(IExtension extension)
    {
        using var stream = extension.GetType().Assembly.GetManifestResourceStream(ExtensionSdk.ManifestResource)
            ?? throw new InvalidOperationException(
                $"Extension {extension.GetType().FullName} has no manifest: embed extension.json with LogicalName '{ExtensionSdk.ManifestResource}'.");
        return ExtensionManifest.Parse(stream);
    }
}

/// <summary>Registers contributions; every one is gated by the tenant's enablement of the extension.</summary>
internal sealed class ExtensionBuilder(LoadedExtension extension, IServiceCollection services, IConfiguration configuration) : IExtensionBuilder
{
    // PostgreSQL identifiers have at most 63 characters; keep room for the table name on SQLite.
    private const int MaxSchemaLength = 40;

    public ExtensionManifest Manifest => extension.Manifest;

    public IServiceCollection Services => services;

    public IConfiguration Configuration => configuration;

    public IExtensionBuilder AddFieldType(IFieldType fieldType)
    {
        RequirePrefix(fieldType.Name, "Field type");
        services.AddSingleton<IFieldType>(fieldType);
        extension.Contributions.FieldTypes.Add(fieldType.Name);
        return this;
    }

    public IExtensionBuilder AddItemReceiver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReceiver>(Action<ItemReceiverOptions>? configure = null)
        where TReceiver : class, IItemEventReceiver
    {
        var options = new ItemReceiverOptions();
        configure?.Invoke(options);
        var id = extension.Id;
        services.TryAddScoped<TReceiver>();
        services.AddScoped<IItemEventReceiver>(sp => new GatedItemReceiver(id, options, sp.GetRequiredService<TReceiver>(), sp.GetRequiredService<IExtensionState>()));
        extension.Contributions.ItemReceivers.Add(typeof(TReceiver).Name);
        return this;
    }

    public IExtensionBuilder AddEventSubscriber<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSubscriber>()
        where TEvent : IntegrationEvent
        where TSubscriber : class, IEventSubscriber<TEvent>
    {
        var id = extension.Id;
        services.TryAddScoped<TSubscriber>();
        services.AddScoped<IEventSubscriber<TEvent>>(sp => new GatedEventSubscriber<TEvent>(id, sp.GetRequiredService<TSubscriber>(), sp.GetRequiredService<IExtensionState>()));
        extension.Contributions.EventSubscribers.Add($"{typeof(TEvent).Name}: {typeof(TSubscriber).Name}");
        return this;
    }

    public IExtensionBuilder AddIntegrationEvent<TEvent>()
        where TEvent : IntegrationEvent
    {
        services.AddIntegrationEvent<TEvent>();
        return this;
    }

    public IExtensionBuilder AddRecurringJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(string name, string cronSchedule)
        where TJob : class, ITenantRecurringJob
    {
        RequirePrefix(name, "Job name");
        services.TryAddScoped<TJob>();
        services.AddSingleton(new ExtensionJobOwner(typeof(TJob), extension.Id));
        services.AddTenantRecurringJob<GatedRecurringJob<TJob>>(name, cronSchedule);
        extension.Contributions.Jobs.Add(name);
        return this;
    }

    public IExtensionBuilder AddContentType(ContentTypeTemplate contentType)
    {
        RequirePrefix(contentType.Key, "Content type key");
        services.AddSingleton(contentType with { ExtensionId = extension.Id });
        extension.Contributions.ContentTypes.Add(contentType.Key);
        return this;
    }

    public IExtensionBuilder AddListTemplate(ListTemplateDefinition listTemplate)
    {
        RequirePrefix(listTemplate.Key, "List template key");
        services.AddSingleton(listTemplate with { ExtensionId = extension.Id });
        extension.Contributions.ListTemplates.Add(listTemplate.Key);
        return this;
    }

    public IExtensionBuilder AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>()
        where TContext : ExtensionDbContext
    {
        if (extension.Contributions.DbContext is { } existing)
        {
            throw new InvalidOperationException($"Extension {extension.Id} already has a DbContext ({existing}); use one per extension.");
        }

        var schema = ExtensionDbContext.SchemaFor(extension.Id);
        if (schema.Length > MaxSchemaLength)
        {
            throw new InvalidOperationException($"The schema of extension {extension.Id} ('{schema}') is longer than {MaxSchemaLength} characters; use a shorter id.");
        }

        services.AddModuleDbContext<TContext>(schema, $"{typeof(TContext).Assembly.GetName().Name}.Migrations");
        extension.Contributions.DbContext = typeof(TContext).Name;
        return this;
    }

    public IExtensionBuilder AddAutomationAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAction>()
        where TAction : class, IAutomationAction
    {
        var id = extension.Id;
        services.TryAddScoped<TAction>();
        services.AddScoped<IAutomationAction>(sp => new GatedAutomationAction(id, sp.GetRequiredService<TAction>(), sp.GetRequiredService<IExtensionState>()));
        extension.Contributions.AutomationActions.Add(typeof(TAction).Name);
        return this;
    }

    public IExtensionBuilder AddAutomationTrigger(AutomationTriggerDefinition trigger)
    {
        RequirePrefix(trigger.Key, "Automation trigger key");
        services.AddSingleton(trigger);
        extension.Contributions.AutomationTriggers.Add(trigger.Key);
        return this;
    }

    public IExtensionBuilder AddTemplateHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, ITemplateHandler
    {
        var id = extension.Id;
        services.TryAddScoped<THandler>();
        services.AddScoped<ITemplateHandler>(sp => new GatedTemplateHandler(id, sp.GetRequiredService<THandler>(), sp.GetRequiredService<IExtensionState>()));
        extension.Contributions.TemplateHandlers.Add(typeof(THandler).Name);
        return this;
    }

    public IExtensionBuilder MapEndpoints(Action<IEndpointRouteBuilder> map)
    {
        extension.EndpointMaps.Add(map);
        extension.Contributions.Endpoints = true;
        return this;
    }

    private void RequirePrefix(string name, string what)
    {
        if (!name.StartsWith(extension.Id + ".", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what} '{name}' of extension {extension.Id} must start with '{extension.Id}.'.");
        }
    }
}

/// <summary>Runs an extension's item receiver only where its registration and the tenant allow (EVT-03).</summary>
internal sealed class GatedItemReceiver(string extensionId, ItemReceiverOptions options, IItemEventReceiver inner, IExtensionState state) : IItemEventReceiver
{
    public int Sequence => options.Sequence;

    public bool AppliesTo(ItemEventScope scope) =>
        (options.IncludeFolders || !scope.IsFolder)
        && (options.ContentTypes.Count == 0 || (scope.ContentTypeName is { } name && options.ContentTypes.Contains(name, StringComparer.OrdinalIgnoreCase)))
        && (options.Lists.Count == 0 || options.Lists.Contains(scope.ListName, StringComparer.OrdinalIgnoreCase))
        && (options.ListTemplates.Count == 0 || (scope.ListTemplate is { } template && options.ListTemplates.Contains(template, StringComparer.Ordinal)))
        && (options.Condition?.Invoke(scope) ?? true)
        && inner.AppliesTo(scope);

    public async ValueTask<bool> AppliesToAsync(ItemEventScope scope, CancellationToken cancellationToken) =>
        AppliesTo(scope) && await state.IsEnabledAsync(extensionId, cancellationToken) && await inner.AppliesToAsync(scope, cancellationToken);

    public ValueTask ItemAddingAsync(ItemChangingContext context, CancellationToken cancellationToken) => inner.ItemAddingAsync(context, cancellationToken);

    public ValueTask ItemUpdatingAsync(ItemChangingContext context, CancellationToken cancellationToken) => inner.ItemUpdatingAsync(context, cancellationToken);

    public ValueTask ItemDeletingAsync(ItemChangingContext context, CancellationToken cancellationToken) => inner.ItemDeletingAsync(context, cancellationToken);

    public ValueTask ItemAddedAsync(ItemChangedContext context, CancellationToken cancellationToken) => inner.ItemAddedAsync(context, cancellationToken);

    public ValueTask ItemUpdatedAsync(ItemChangedContext context, CancellationToken cancellationToken) => inner.ItemUpdatedAsync(context, cancellationToken);

    public ValueTask ItemDeletedAsync(ItemChangedContext context, CancellationToken cancellationToken) => inner.ItemDeletedAsync(context, cancellationToken);
}

/// <summary>Delivers events to an extension only in tenants that enabled it.</summary>
internal sealed class GatedEventSubscriber<TEvent>(string extensionId, IEventSubscriber<TEvent> inner, IExtensionState state) : IEventSubscriber<TEvent>
    where TEvent : IntegrationEvent
{
    public async Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (await state.IsEnabledAsync(extensionId, cancellationToken))
        {
            await inner.HandleAsync(integrationEvent, cancellationToken);
        }
    }
}

internal sealed record ExtensionJobOwner(Type JobType, string ExtensionId);

/// <summary>Runs an extension's recurring job only in tenants that enabled it.</summary>
internal sealed class GatedRecurringJob<TJob>(TJob inner, IExtensionState state, IEnumerable<ExtensionJobOwner> owners) : ITenantRecurringJob
    where TJob : class, ITenantRecurringJob
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var owner = owners.First(o => o.JobType == typeof(TJob)).ExtensionId;
        if (await state.IsEnabledAsync(owner, cancellationToken))
        {
            await inner.RunAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Runs an extension's template section only where the extension is enabled, or is being enabled by
/// the same template (its Extensions section registers it).
/// </summary>
internal sealed class GatedTemplateHandler(string extensionId, ITemplateHandler inner, IExtensionState state) : ITemplateHandler
{
    public System.Xml.Linq.XName Element => inner.Element;

    public TemplateLevel Level => inner.Level;

    public int Order => inner.Order;

    public async Task<System.Xml.Linq.XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken) =>
        await state.IsEnabledAsync(extensionId, cancellationToken) ? await inner.ExportAsync(context, cancellationToken) : null;

    public async Task ApplyAsync(System.Xml.Linq.XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Resolve(TemplateKinds.Extension, extensionId) is not null || await state.IsEnabledAsync(extensionId, cancellationToken))
        {
            await inner.ApplyAsync(section, context, cancellationToken);
        }
        else
        {
            context.Warn($"Section {section.Name} was skipped: the extension '{extensionId}' is not enabled.", section);
        }
    }
}

/// <summary>
/// An extension's automation action: its key must start with the extension id (checked when first used,
/// since the key is an instance property), and it fails where the extension is not enabled.
/// </summary>
internal sealed class GatedAutomationAction(string extensionId, IAutomationAction inner, IExtensionState state) : IAutomationAction
{
    public string Key => inner.Key.StartsWith(extensionId + ".", StringComparison.Ordinal)
        ? inner.Key
        : throw new InvalidOperationException($"Automation action '{inner.Key}' of extension {extensionId} must start with '{extensionId}.'.");

    public string Description => inner.Description;

    public IEnumerable<string> Validate(System.Text.Json.Nodes.JsonObject inputs) => inner.Validate(inputs);

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken) =>
        await state.IsEnabledAsync(extensionId, cancellationToken)
            ? await inner.ExecuteAsync(context, cancellationToken)
            : AutomationActionResult.Fail($"The extension '{extensionId}' is not enabled.");
}
