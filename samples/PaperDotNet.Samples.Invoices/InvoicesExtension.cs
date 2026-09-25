using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Extensions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Samples.Invoices;

[assembly: PaperDotNetExtension(typeof(InvoicesExtension))]

namespace PaperDotNet.Samples.Invoices;

/// <summary>Registers the sample's contributions; each is active only where a tenant enabled it.</summary>
public sealed class InvoicesExtension : IExtension
{
    public const string Id = "samples.invoices";

    public void Configure(IExtensionBuilder builder)
    {
        builder.Services.AddSingleton<InvoiceStats>();
        builder.AddDbContext<InvoicesDbContext>();
        builder.AddFieldType(new IbanFieldType());

        // Provisioned into a tenant when it enables the extension; managed by the extension.
        builder.AddContentType(new ContentTypeTemplate($"{Id}.invoice", "Invoice", "An invoice with amount, approval status and IBAN.",
        [
            new FieldDefinition { Name = "amount", DisplayName = "Amount", Type = "number", Required = true },
            new FieldDefinition { Name = "status", DisplayName = "Status", Type = "choice", Choices = ["draft", "pendingApproval", "approved"], DefaultValue = "\"draft\"" },
            new FieldDefinition { Name = "iban", DisplayName = "IBAN", Type = $"{Id}.iban", Search = FieldSearchWeight.High },
            new FieldDefinition { Name = "internalNote", DisplayName = "Internal note", Type = "note", Search = FieldSearchWeight.None },
        ]));
        builder.AddListTemplate(new ListTemplateDefinition($"{Id}.invoices", "Invoices", "Invoices with an approval queue.", [$"{Id}.invoice"],
        [
            new ViewTemplate("All invoices", ["title", "amount", "status"], OrderBy: "fields/amount desc", IsDefault: true),
            new ViewTemplate("Needs approval", ["title", "amount"], "fields/status eq 'pendingApproval'"),
        ])
        {
            Versioning = true,
        });
        builder.AddTermSet(new Taxonomy.Contracts.TermSetTemplate("Invoices", "Cost centers",
        [
            new("Operations", Children: [new("Facilities"), new("IT", ["Information technology"])]),
            new("Sales", Children: [new("Marketing")]),
        ], "Cost centers for invoices.")
        {
            Key = $"{Id}.costCenters",
        });
        builder.AddItemMutator<ApprovalMutator>(o =>
        {
            o.Sequence = 100;
            o.ContentTypes.Add("Invoice");
        });
        builder.AddEventSubscriber<ItemAdded, InvoiceCounter>();
        builder.AddAutomationTrigger(new(ApprovalNeededTrigger.Key, "An invoice above the approval threshold was added (data: amount)."));
        builder.AddAutomationAction<ApproveInvoiceAction>();
        builder.AddMcpTool<PendingInvoicesTool>();
        builder.AddEventSubscriber<ItemAdded, ApprovalNeededTrigger>();
        builder.AddRecurringJob<ReminderJob>($"{Id}.reminders", "* * * * * *");
        builder.MapEndpoints(api => api.MapGet("/stats", (InvoiceStats stats, ITenantContext tenant) => TypedResults.Ok(stats.For(tenant.TenantId!.Value)))
            .RequireScope($"{Id}.read"));
        builder.MapEndpoints(ApprovalEndpoints.Map);
    }
}

/// <summary>An IBAN (ISO 13616): stored without spaces, upper case, checked with mod 97.</summary>
public sealed class IbanFieldType : FieldType
{
    public override string Name => $"{InvoicesExtension.Id}.iban";

    public override FieldValueKind ValueKind => FieldValueKind.Text;

    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken)
    {
        var iban = value.ValueKind == JsonValueKind.String ? value.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() : null;
        return ValueTask.FromResult(iban is not null && IsValid(iban)
            ? FieldValueResult.Ok(JsonValue.Create(iban))
            : FieldValueResult.Fail("A valid IBAN is expected."));
    }

    public static bool IsValid(string iban)
    {
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) % 97 == 1;
    }
}

/// <summary>Item mutator: invoices above the tenant's threshold get the status <c>pendingApproval</c>.</summary>
public sealed class ApprovalMutator(IExtensionState state) : IItemMutator
{
    public int Sequence => 100;

    public ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ApplyAsync(context, cancellationToken);

    public ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ApplyAsync(context, cancellationToken);

    private async ValueTask ApplyAsync(ItemMutationContext context, CancellationToken ct)
    {
        var settings = await state.GetSettingsAsync(InvoicesExtension.Id, ct);
        if (settings["requireApproval"]?.GetValue<bool>() != true || context.After?["amount"] is not JsonValue amount)
        {
            return;
        }

        if (amount.GetValue<decimal>() > settings["approvalThreshold"]!.GetValue<decimal>() && context.After["status"]?.GetValue<string>() != "approved")
        {
            context.After["status"] = "pendingApproval";
        }
    }
}

/// <summary>In-memory statistics per tenant (lost on restart; durable data goes to <see cref="InvoicesDbContext"/>).</summary>
public sealed class InvoiceStats
{
    private readonly ConcurrentDictionary<Guid, Counters> _tenants = new();

    public Counters For(Guid tenantId) => _tenants.GetOrAdd(tenantId, _ => new Counters());

    public sealed class Counters
    {
        private int _itemsAdded;
        private int _reminderRuns;
        private int _pendingApprovals;

        public int ItemsAdded => _itemsAdded;

        public int ReminderRuns => _reminderRuns;

        /// <summary>Invoices waiting for approval in all invoice lists, as of the last reminder run.</summary>
        public int PendingApprovals => _pendingApprovals;

        internal void ItemAdded() => Interlocked.Increment(ref _itemsAdded);

        internal void ReminderRan(int pendingApprovals)
        {
            Interlocked.Exchange(ref _pendingApprovals, pendingApprovals);
            Interlocked.Increment(ref _reminderRuns);
        }
    }
}

/// <summary>Asynchronous subscriber: counts added items.</summary>
public sealed class InvoiceCounter(InvoiceStats stats) : IEventSubscriber<ItemAdded>
{
    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken)
    {
        stats.For(integrationEvent.TenantId).ItemAdded();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recurring job (every second in this sample): counts invoices waiting for approval in every
/// invoice list of the tenant. Jobs run without a user, so it reads as the system.
/// </summary>
public sealed class ReminderJob(InvoiceStats stats, ITenantContext tenant, IListItemStore items) : ITenantRecurringJob
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var system = items.AsSystem();
        var pending = 0;
        foreach (var list in await system.GetListsAsync(null, $"{InvoicesExtension.Id}.invoices", cancellationToken))
        {
            var (found, _) = await system.QueryAsync(list.WorkspaceId, list.Id, new ListItemQuery("fields/status eq 'pendingApproval'", Top: 1000), cancellationToken);
            pending += found.Count;
        }

        stats.For(tenant.TenantId!.Value).ReminderRan(pending);
    }
}
