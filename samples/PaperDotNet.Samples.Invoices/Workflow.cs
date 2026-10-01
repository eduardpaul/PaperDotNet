using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Samples.Invoices;

/// <summary>
/// Action <c>samples.invoices.approve</c> (EVT-09): marks the run's invoice as approved, as the run's actor. Activities
/// are singletons: scoped services come from <see cref="WorkflowActivityContext.Services"/>.
/// </summary>
public sealed class ApproveInvoiceAction : IWorkflowActivity
{
    public string Key => $"{InvoicesExtension.Id}.approve";

    public string Description => "Marks the invoice as approved.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.ListId is not { } listId || context.ItemId is not { } itemId)
        {
            return WorkflowActivityResult.Fail("An invoice is required.");
        }

        var items = context.Services.GetRequiredService<IListItemStore>().AsSystem(context.Actor);
        var result = await items.UpdateAsync(context.WorkspaceId, listId, itemId, new JsonObject { ["status"] = "approved" }, null, cancellationToken);
        return result.Succeeded ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail(result.Describe());
    }
}
