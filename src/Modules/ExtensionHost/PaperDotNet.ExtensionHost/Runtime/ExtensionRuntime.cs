using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Extensions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.ExtensionHost.Runtime;

/// <summary>What an extension contributes (for the catalog API).</summary>
public sealed class ExtensionContributions
{
    public List<string> FieldTypes { get; } = [];

    public List<string> ItemMutators { get; } = [];

    public List<string> EventSubscribers { get; } = [];

    public List<string> Jobs { get; } = [];

    public List<string> ContentTypes { get; } = [];

    public List<string> ListTemplates { get; } = [];

    public List<string> TermSets { get; } = [];

    public List<string> TemplateSections { get; } = [];

    public List<string> WorkflowActivities { get; } = [];

    public List<string> WorkflowTriggers { get; } = [];

    public List<string> Tables { get; } = [];

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

/// <summary>JSON metadata an extension brings for its endpoints (<see cref="IExtensionBuilder.AddJson"/>).</summary>
internal sealed record ExtensionJson(IJsonTypeInfoResolver Resolver);

public static class ExtensionRegistration
{
    public const string RoutePrefix = "/v1.0/ext";

    /// <summary>
    /// Validates the manifests of <paramref name="extensions"/> and lets each one register its contributions. Fails fast
    /// at startup on invalid or conflicting extensions.
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

        // The extensions' JSON metadata comes after the modules' (which the host inserts first).
        services.AddOptions<JsonOptions>().Configure<IEnumerable<ExtensionJson>>((options, json) =>
        {
            foreach (var resolver in json)
            {
                options.SerializerOptions.TypeInfoResolverChain.Add(resolver.Resolver);
            }
        });
        return services;
    }

    /// <summary>Maps extension endpoints under <c>/v1.0/ext/{id}</c>, answering 404 where the extension is disabled.</summary>
    public static IEndpointRouteBuilder MapPaperDotNetExtensions(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.ServiceProvider.GetRequiredService<ExtensionCatalog>();
        foreach (var extension in catalog.All.Where(e => e.EndpointMaps.Count > 0))
        {
            var id = extension.Id;
            var group = endpoints.MapGroup($"{RoutePrefix}/{id}").WithTags($"Extension: {extension.Manifest.Name}");
            group.AddEndpointFilter(async (context, next) =>
            {
                var services = context.HttpContext.RequestServices;
                var tenant = services.GetRequiredService<ICurrentUser>().TenantId;
                return tenant is { } tenantId && await services.GetRequiredService<IExtensionState>().IsEnabledAsync(tenantId, id, context.HttpContext.RequestAborted)
                    ? await next(context)
                    : ApiErrors.Problem(StatusCodes.Status404NotFound, "extensionDisabled", "The extension is not enabled for this organization.");
            });
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
    public ExtensionManifest Manifest => extension.Manifest;

    public IServiceCollection Services => services;

    public IConfiguration Configuration => configuration;

    public IExtensionBuilder AddJson(IJsonTypeInfoResolver json)
    {
        services.AddSingleton(new ExtensionJson(json));
        return this;
    }

    public IExtensionBuilder AddFieldType(IFieldType fieldType)
    {
        RequirePrefix(fieldType.Name, "Field type");
        services.AddSingleton(fieldType);
        extension.Contributions.FieldTypes.Add(fieldType.Name);
        return this;
    }

    public IExtensionBuilder AddItemMutator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMutator>(Action<ItemMutatorOptions>? configure = null)
        where TMutator : class, IItemMutator
    {
        var options = new ItemMutatorOptions();
        configure?.Invoke(options);
        var id = extension.Id;
        services.TryAddScoped<TMutator>();
        services.AddScoped<IItemMutator>(sp => new GatedItemMutator(id, options, sp.GetRequiredService<TMutator>(), sp.GetRequiredService<IExtensionState>()));
        extension.Contributions.ItemMutators.Add(typeof(TMutator).Name);
        return this;
    }

    public IExtensionBuilder AddEventSubscriber<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSubscriber>()
        where TEvent : IntegrationEvent
        where TSubscriber : class, IEventSubscriber<TEvent>
    {
        if (!ExtensionEvents.Supported.Contains(typeof(TEvent)))
        {
            throw new InvalidOperationException(
                $"Extension {extension.Id}: {typeof(TEvent).Name} cannot be subscribed to; supported are {string.Join(", ", ExtensionEvents.Supported.Select(t => t.Name))}.");
        }

        var id = extension.Id;
        services.TryAddScoped<TSubscriber>();
        services.AddScoped(sp => new ExtensionSubscription<TEvent>(id, typeof(TSubscriber).Name, sp.GetRequiredService<TSubscriber>()));
        extension.Contributions.EventSubscribers.Add($"{typeof(TEvent).Name}: {typeof(TSubscriber).Name}");
        return this;
    }

    public IExtensionBuilder AddRecurringJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(string name, string cronSchedule)
        where TJob : class, ITenantRecurringJob
    {
        RequirePrefix(name, "Job name");
        var id = extension.Id;
        services.TryAddScoped<TJob>();
        services.AddSingleton(new RecurringJobRegistration(name, cronSchedule, sp => new GatedRecurringJob(id, sp.GetRequiredService<TJob>(), sp.GetRequiredService<IExtensionState>())));
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

    public IExtensionBuilder AddTermSet(Taxonomy.Contracts.TermSetTemplate termSet)
    {
        RequirePrefix(termSet.Key ?? "", "Term set key");
        services.AddSingleton(termSet with { ExtensionId = extension.Id });
        extension.Contributions.TermSets.Add(termSet.Key!);
        return this;
    }

    public IExtensionBuilder AddListTemplate(ListTemplateDefinition listTemplate)
    {
        RequirePrefix(listTemplate.Key, "List template key");
        services.AddSingleton(listTemplate with { ExtensionId = extension.Id });
        extension.Contributions.ListTemplates.Add(listTemplate.Key);
        return this;
    }

    public IExtensionBuilder AddTemplateSection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, Provisioning.Contracts.ITemplateHandler
    {
        var id = extension.Id;
        services.TryAddScoped<THandler>();
        services.AddScoped<Provisioning.Contracts.ITemplateHandler>(sp => new Features.GatedTemplateHandler(id, sp.GetRequiredService<THandler>(), sp.GetRequiredService<IExtensionState>()));
        extension.Contributions.TemplateSections.Add(typeof(THandler).Name);
        return this;
    }

    public IExtensionBuilder AddWorkflowTrigger(WorkflowTriggerDefinition trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!trigger.Key.StartsWith($"{extension.Id}.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The trigger '{trigger.Key}' of the extension '{extension.Id}' must start with '{extension.Id}.'.");
        }

        services.AddSingleton(trigger);
        extension.Contributions.WorkflowTriggers.Add(trigger.Key);
        return this;
    }

    public IExtensionBuilder AddWorkflowActivity<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TActivity>()
        where TActivity : class, IWorkflowActivity
    {
        var id = extension.Id;
        services.TryAddSingleton<TActivity>();
        services.AddSingleton<IWorkflowActivity>(sp => new GatedWorkflowActivity(id, sp.GetRequiredService<TActivity>()));
        extension.Contributions.WorkflowActivities.Add(typeof(TActivity).Name);
        return this;
    }

    public IExtensionBuilder AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>()
        where TContext : ExtensionDbContext
    {
        services.AddModuleDbContext<TContext>();
        services.AddSingleton(new SchemaScripts(extension.Id, typeof(TContext).Assembly));
        extension.Contributions.Tables.Add(typeof(TContext).Name);
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

/// <summary>Runs an extension's item mutator only where its registration and the tenant allow (EVT-03).</summary>
internal sealed class GatedItemMutator(string extensionId, ItemMutatorOptions options, IItemMutator inner, IExtensionState state) : IItemMutator
{
    public int Sequence => options.Sequence;

    public bool AppliesTo(ItemEventScope scope) =>
        (options.IncludeFolders || !scope.IsFolder)
        && (options.ContentTypes.Count == 0
            || (scope.ContentTypeKey is { } key && options.ContentTypes.Contains(key, StringComparer.Ordinal))
            || (scope.ContentTypeName is { } name && options.ContentTypes.Contains(name, StringComparer.OrdinalIgnoreCase)))
        && (options.Lists.Count == 0 || options.Lists.Contains(scope.ListName, StringComparer.OrdinalIgnoreCase))
        && (options.ListTemplates.Count == 0 || (scope.ListTemplate is { } template && options.ListTemplates.Contains(template, StringComparer.Ordinal)))
        && (options.Condition?.Invoke(scope) ?? true)
        && inner.AppliesTo(scope);

    public async ValueTask<bool> AppliesToAsync(ItemEventScope scope, CancellationToken cancellationToken) =>
        AppliesTo(scope) && await state.IsEnabledAsync(scope.TenantId, extensionId, cancellationToken) && await inner.AppliesToAsync(scope, cancellationToken);

    public ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken) => inner.ItemAddingAsync(context, cancellationToken);

    public ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken) => inner.ItemUpdatingAsync(context, cancellationToken);

    public ValueTask ItemDeletingAsync(ItemMutationContext context, CancellationToken cancellationToken) => inner.ItemDeletingAsync(context, cancellationToken);
}

/// <summary>An extension's subscriber of <typeparamref name="TEvent"/>, delivered by <see cref="ExtensionEvents"/>.</summary>
internal sealed record ExtensionSubscription<TEvent>(string ExtensionId, string Name, IEventSubscriber<TEvent> Subscriber)
    where TEvent : IntegrationEvent;

/// <summary>Runs an extension's recurring job only in tenants that enabled it.</summary>
internal sealed class GatedRecurringJob(string extensionId, ITenantRecurringJob inner, IExtensionState state) : ITenantRecurringJob
{
    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await state.IsEnabledAsync(tenantId, extensionId, cancellationToken))
        {
            await inner.RunAsync(tenantId, cancellationToken);
        }
    }
}

/// <summary>
/// An extension's workflow activity: its key must start with the extension id (checked when first used, since the key
/// is an instance property), and it fails where the extension is not enabled.
/// </summary>
internal sealed class GatedWorkflowActivity(string extensionId, IWorkflowActivity inner) : IWorkflowActivity
{
    public string Key => inner.Key.StartsWith(extensionId + ".", StringComparison.Ordinal)
        ? inner.Key
        : throw new InvalidOperationException($"Workflow activity '{inner.Key}' of extension {extensionId} must start with '{extensionId}.'.");

    public string Description => inner.Description;

    public IEnumerable<string> Validate(System.Text.Json.Nodes.JsonObject inputs) => inner.Validate(inputs);

    public IReadOnlyList<string> Outcomes => inner.Outcomes;

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken) =>
        await context.Services.GetRequiredService<IExtensionState>().IsEnabledAsync(context.TenantId, extensionId, cancellationToken)
            ? await inner.ExecuteAsync(context, cancellationToken)
            : WorkflowActivityResult.Fail($"The extension '{extensionId}' is not enabled.");
}
