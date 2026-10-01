using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Samples.Invoices;

/// <summary>
/// Activity <c>samples.invoices.awaitPayment</c> (ADR-0036): the run waits until the invoice's status becomes
/// <c>paid</c> (<see cref="PaymentReceived"/> completes the wait) or <c>days</c> pass (default 30). Ports: <c>paid</c> and
/// <c>timeout</c>. One payment wait per invoice.
/// </summary>
public sealed class AwaitPaymentActivity(TimeProvider time) : IWorkflowActivity
{
    public const string WaitKind = $"{InvoicesExtension.Id}.payment";

    public string Key => $"{InvoicesExtension.Id}.awaitPayment";

    public string Description => "Waits until the invoice is paid or { \"days\": 30 } pass (ports paid and timeout).";

    public IReadOnlyList<string> Outcomes => ["paid", "timeout"];

    public IEnumerable<string> Validate(JsonObject inputs) =>
        inputs["days"] is null || (inputs["days"] is JsonValue days && days.GetValueKind() == JsonValueKind.Number) ? [] : ["days must be a number."];

    public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.ItemId is not { } itemId)
        {
            return Task.FromResult(WorkflowActivityResult.Fail("An invoice is required."));
        }

        var days = ActivityInputs.Number(context.Inputs, "days") ?? 30;
        return Task.FromResult(WorkflowActivityResult.Wait(WaitKind, itemId.ToString("N"), time.GetUtcNow().AddDays(days)));
    }
}

/// <summary>Completes an invoice's payment wait (<see cref="AwaitPaymentActivity"/>) when its status becomes <c>paid</c>.</summary>
public sealed class PaymentReceived(IListItemStore items, IWorkflowBookmarks bookmarks) : IEventSubscriber<ItemUpdated>
{
    public async Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken)
    {
        if (!integrationEvent.ChangedFields.Contains("status"))
        {
            return;
        }

        var actor = new ChangeActor(integrationEvent.TenantId, null, integrationEvent.Depth + 1);
        var item = await items.AsSystem(actor).GetAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId, cancellationToken);
        if (item?.Fields["status"] is JsonValue status && status.TryGetValue<string>(out var value) && value == "paid")
        {
            await bookmarks.CompleteAsync(integrationEvent.TenantId, AwaitPaymentActivity.WaitKind, item.Id.ToString("N"), new JsonObject { ["outcome"] = "paid" }, cancellationToken);
        }
    }
}

/// <summary>
/// Raises the trigger <c>samples.invoices.approvalNeeded</c> (EVT-09) when an invoice above the threshold is added, so
/// that workflows can react, e.g. with an approval. Raised for the item event (an id made from it and its depth), so a
/// redelivered event starts nothing twice.
/// </summary>
public sealed class ApprovalNeededTrigger(IListItemStore items, IWorkflowTriggers triggers) : IEventSubscriber<ItemAdded>
{
    public const string Key = $"{InvoicesExtension.Id}.approvalNeeded";

    public async Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken)
    {
        var actor = new ChangeActor(integrationEvent.TenantId, null, integrationEvent.Depth);
        var item = await items.AsSystem(actor).GetAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId, cancellationToken);
        if (item?.Fields["status"] is not JsonValue status || !status.TryGetValue<string>(out var value) || value != "pendingApproval")
        {
            return;
        }

        await triggers.RaiseAsync(Key, integrationEvent.WorkspaceId, new WorkflowItem(item.WorkspaceId, item.ListId, item.Id),
            new JsonObject { ["amount"] = item.Fields["amount"]?.DeepClone() }, integrationEvent, cancellationToken);
    }
}
