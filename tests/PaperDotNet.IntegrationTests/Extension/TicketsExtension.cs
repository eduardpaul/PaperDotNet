using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaperDotNet.Api;
using PaperDotNet.Extensions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.IntegrationTests.Extension;

/// <summary>A small extension using every extension point of the SDK core (EXT-04), for the integration tests.</summary>
public sealed class TicketsExtension : IExtension
{
    public const string Id = "tests.tickets";

    public void Configure(IExtensionBuilder builder)
    {
        builder.AddJson(TicketsJson.Default);
        builder.AddFieldType(new TicketCodeFieldType());
        builder.AddContentType(new ContentTypeTemplate($"{Id}.ticket", "Ticket", "A support ticket.",
        [
            new FieldDefinition { Name = "code", DisplayName = "Code", Type = $"{Id}.code" },
            new FieldDefinition { Name = "priority", DisplayName = "Priority", Type = "choice", Choices = ["low", "high"] },
        ]));
        builder.AddTermSet(new Taxonomy.Contracts.TermSetTemplate("Support", "Ticket areas",
        [
            new("Hardware", Children: [new("Printers"), new("Laptops", ["Notebooks"])]),
            new("Software"),
        ], "Areas of support tickets.")
        {
            Key = $"{Id}.areas",
        });
        builder.AddListTemplate(new ListTemplateDefinition($"{Id}.tickets", "Tickets", "Support tickets.", [$"{Id}.ticket"],
        [
            new ViewTemplate("All tickets", ["title", "code", "priority"], IsDefault: true),
        ]));
        builder.AddItemMutator<TicketPrefixMutator>(o => o.ContentTypes.Add("Ticket"));
        builder.AddEventSubscriber<ItemAdded, TicketCounter>();
        builder.AddRecurringJob<TicketJob>($"{Id}.tick", "* * * * * *");
        builder.AddWorkflowActivity<EchoActivity>();
        builder.AddTemplateSection<TicketMarkerSection>();
        builder.MapEndpoints(api => api.MapGet("/stats", (Caller caller) => TypedResults.Ok(new TicketStats(TicketCounter.Count(caller.TenantId))))
            .RequireScope($"{Id}.read"));
    }
}

/// <summary>A ticket code: upper case, letters and digits.</summary>
public sealed class TicketCodeFieldType : FieldType
{
    public override string Name => $"{TicketsExtension.Id}.code";

    public override FieldValueKind ValueKind => FieldValueKind.Text;

    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } code && code.All(char.IsAsciiLetterOrDigit)
            ? FieldValueResult.Ok(JsonValue.Create(code.ToUpperInvariant()))
            : FieldValueResult.Fail("Letters and digits are expected."));
}

/// <summary>Puts the tenant's prefix setting before the titles of new tickets.</summary>
public sealed class TicketPrefixMutator(IExtensionState state) : IItemMutator
{
    public async ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken)
    {
        var prefix = (await state.GetSettingsAsync(context.TenantId, TicketsExtension.Id, cancellationToken))["prefix"]?.GetValue<string>() ?? "";
        var title = context.After!["title"]!.GetValue<string>();
        if (!title.StartsWith(prefix, StringComparison.Ordinal))
        {
            context.After["title"] = prefix + title;
        }
    }
}

/// <summary>Counts added items per tenant (delivered in the background by the host).</summary>
public sealed class TicketCounter : IEventSubscriber<ItemAdded>
{
    private static readonly ConcurrentDictionary<Guid, int> Counts = new();

    public static int Count(Guid tenantId) => Counts.GetValueOrDefault(tenantId);

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken)
    {
        Counts.AddOrUpdate(integrationEvent.TenantId, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

/// <summary>A recurring job that records the tenants it ran for.</summary>
public sealed class TicketJob : ITenantRecurringJob
{
    public static readonly ConcurrentDictionary<Guid, int> Runs = new();

    public Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Runs.AddOrUpdate(tenantId, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

/// <summary>A workflow activity that returns its input.</summary>
public sealed class EchoActivity : IWorkflowActivity
{
    public string Key => $"{TicketsExtension.Id}.echo";

    public string Description => "Returns its text input.";

    public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken) =>
        Task.FromResult(WorkflowActivityResult.Ok(new JsonObject { ["echo"] = context.Inputs["text"]?.DeepClone() }));
}

public sealed record TicketStats(int Added);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TicketStats))]
internal sealed partial class TicketsJson : JsonSerializerContext;

/// <summary>
/// A template section of the extension (PRV-05) in its own namespace: remembers the marker value applied per tenant and
/// exports it again.
/// </summary>
public sealed class TicketMarkerSection : Provisioning.Contracts.ITemplateHandler
{
    public static readonly System.Xml.Linq.XNamespace Ns = "urn:test:marker";

    public static ConcurrentDictionary<Guid, string> Applied { get; } = new();

    public System.Xml.Linq.XName Element => Ns + "Marker";

    public Provisioning.Contracts.TemplateLevel Level => Provisioning.Contracts.TemplateLevel.Tenant;

    public int Order => 1000;

    public Task<System.Xml.Linq.XElement?> ExportAsync(Provisioning.Contracts.TemplateContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Applied.TryGetValue(context.TenantId, out var value) ? new System.Xml.Linq.XElement(Element, new System.Xml.Linq.XAttribute("Value", value)) : null);

    public Task ApplyAsync(System.Xml.Linq.XElement section, Provisioning.Contracts.TemplateContext context, CancellationToken cancellationToken)
    {
        var value = section.Attribute("Value")?.Value ?? "";
        if (!Applied.TryGetValue(context.TenantId, out var current) || current != value)
        {
            context.Updated("marker", value);
            if (!context.DryRun)
            {
                Applied[context.TenantId] = value;
            }
        }

        return Task.CompletedTask;
    }
}
