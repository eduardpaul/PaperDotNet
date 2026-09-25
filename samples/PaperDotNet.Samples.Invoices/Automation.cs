using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Samples.Invoices;

/// <summary>
/// Raises the trigger <c>samples.invoices.approvalNeeded</c> (EVT-09) when an invoice above the threshold is added,
/// so that rules can react, e.g. by starting an approval workflow.
/// </summary>
public sealed class ApprovalNeededTrigger(IListItemStore items, IAutomationTriggers triggers) : IEventSubscriber<ItemAdded>
{
    public const string Key = $"{InvoicesExtension.Id}.approvalNeeded";

    public async Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken)
    {
        var store = items.AsSystem();
        var item = await store.GetAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId, cancellationToken);
        if (item?.Fields["status"]?.GetValue<string>() != "pendingApproval")
        {
            return;
        }

        await triggers.RaiseAsync(Key, integrationEvent.WorkspaceId, new AutomationItem(item.WorkspaceId, item.ListId, item.Id),
            new JsonObject { ["amount"] = item.Fields["amount"]?.DeepClone() }, cancellationToken);
    }
}

/// <summary>Action <c>samples.invoices.approve</c> (EVT-09): marks the invoice as approved.</summary>
public sealed class ApproveInvoiceAction(IListItemStore items) : IAutomationAction
{
    public string Key => $"{InvoicesExtension.Id}.approve";

    public string Description => "Marks the invoice as approved.";

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return AutomationActionResult.Fail("An invoice is required.");
        }

        var result = await items.AsSystem().UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { ["status"] = "approved" }, null, cancellationToken);
        return result.Succeeded ? AutomationActionResult.Ok() : AutomationActionResult.Fail(result.Status.ToString());
    }
}
