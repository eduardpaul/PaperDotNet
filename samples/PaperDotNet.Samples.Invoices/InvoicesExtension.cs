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
        builder.AddFieldType(new IbanFieldType());
        builder.AddItemReceiver<ApprovalReceiver>(o =>
        {
            o.Sequence = 100;
            o.ContentTypes.Add("Invoice");
        });
        builder.AddEventSubscriber<ItemAdded, InvoiceCounter>();
        builder.AddRecurringJob<ReminderJob>($"{Id}.reminders", "* * * * * *");
        builder.MapEndpoints(api => api.MapGet("/stats", (InvoiceStats stats, ITenantContext tenant) => TypedResults.Ok(stats.For(tenant.TenantId!.Value)))
            .RequireScope($"{Id}.read"));
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

/// <summary>Before-receiver: invoices above the tenant's threshold get the status <c>pendingApproval</c>.</summary>
public sealed class ApprovalReceiver(IExtensionState state) : IItemEventReceiver
{
    public int Sequence => 100;

    public ValueTask ItemAddingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ApplyAsync(context, cancellationToken);

    public ValueTask ItemUpdatingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ApplyAsync(context, cancellationToken);

    private async ValueTask ApplyAsync(ItemChangingContext context, CancellationToken ct)
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

/// <summary>In-memory statistics per tenant (the sample keeps no data; see extension storage in 2c).</summary>
public sealed class InvoiceStats
{
    private readonly ConcurrentDictionary<Guid, Counters> _tenants = new();

    public Counters For(Guid tenantId) => _tenants.GetOrAdd(tenantId, _ => new Counters());

    public sealed class Counters
    {
        private int _itemsAdded;
        private int _reminderRuns;

        public int ItemsAdded => _itemsAdded;

        public int ReminderRuns => _reminderRuns;

        internal void ItemAdded() => Interlocked.Increment(ref _itemsAdded);

        internal void ReminderRan() => Interlocked.Increment(ref _reminderRuns);
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

/// <summary>Recurring job (every second in this sample): counts its runs per tenant.</summary>
public sealed class ReminderJob(InvoiceStats stats, ITenantContext tenant) : ITenantRecurringJob
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        stats.For(tenant.TenantId!.Value).ReminderRan();
        return Task.CompletedTask;
    }
}
