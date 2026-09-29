using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Samples.Invoices;

/// <summary>
/// Raises the trigger <c>samples.invoices.approvalNeeded</c> (EVT-09) when an invoice above the threshold is added,
/// so that workflows can react, e.g. with an approval.
/// </summary>
public sealed class ApprovalNeededTrigger(IListItemStore items, IWorkflowTriggers triggers) : IEventSubscriber<ItemAdded>
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

        await triggers.RaiseAsync(Key, integrationEvent.WorkspaceId, new WorkflowItem(item.WorkspaceId, item.ListId, item.Id),
            new JsonObject { ["amount"] = item.Fields["amount"]?.DeepClone() }, cancellationToken);
    }
}

/// <summary>Action <c>samples.invoices.approve</c> (EVT-09): marks the invoice as approved.</summary>
public sealed class ApproveInvoiceAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => $"{InvoicesExtension.Id}.approve";

    public string Description => "Marks the invoice as approved.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("An invoice is required.");
        }

        var result = await items.AsSystem().UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { ["status"] = "approved" }, null, cancellationToken);
        return result.Succeeded ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail(result.Status.ToString());
    }
}

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

    public JsonObject? InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["days"] = new JsonObject { ["type"] = "number", ["description"] = "How long to wait for the payment (default 30)." } },
    };

    public IEnumerable<string> Validate(JsonObject inputs) =>
        inputs["days"] is null || (inputs["days"] is JsonValue days && days.GetValueKind() == JsonValueKind.Number) ? [] : ["days must be a number."];

    public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return Task.FromResult(WorkflowActivityResult.Fail("An invoice is required."));
        }

        var days = context.Inputs["days"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : 30;
        return Task.FromResult(WorkflowActivityResult.Wait(WaitKind, item.ItemId.ToString("N"), time.GetUtcNow().AddDays(days)));
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

        var item = await items.AsSystem().GetAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId, cancellationToken);
        if (item?.Fields["status"]?.GetValue<string>() == "paid")
        {
            await bookmarks.CompleteAsync(AwaitPaymentActivity.WaitKind, item.Id.ToString("N"), new JsonObject { ["outcome"] = "paid" }, cancellationToken);
        }
    }
}

/// <summary>
/// The extension's built-in workflow <c>samples.invoices.approveAndCollect</c> (EVT-12), added with
/// <c>builder.AddWorkflow</c> like the product's own: an invoice above the threshold is approved by <c>approvers</c>, marked
/// approved (<see cref="ApproveInvoiceAction"/>), and the run waits for the payment (<see cref="AwaitPaymentActivity"/>);
/// when it does not come within <c>paymentDays</c>, the approvers are told to chase it. Workspaces turn it on with its
/// parameters or copy it to change it; it is offered only where the extension is enabled.
/// </summary>
public static class InvoiceWorkflows
{
    public static readonly BuiltInWorkflow ApproveAndCollect = new($"{InvoicesExtension.Id}.approveAndCollect", "Approve and collect invoices",
        "Asks for approval of invoices above the threshold, marks them approved and follows up when they are not paid in time.",
        JsonNode.Parse("""
            {
              "trigger": { "type": "samples.invoices.approvalNeeded", "list": "{param:list}" },
              "flow": {
                "start": "approve",
                "nodes": {
                  "approve": { "activity": "approval", "inputs": { "assignees": "{param:approvers}", "title": "Approve {title} ({trigger:amount})" },
                               "next": { "approved": "approved" } },
                  "approved": { "activity": "samples.invoices.approve", "next": { "done": "payment" } },
                  "payment": { "activity": "samples.invoices.awaitPayment", "inputs": { "days": "{param:paymentDays}" }, "next": { "timeout": "chase" } },
                  "chase": { "activity": "notify", "inputs": { "to": "{param:approvers}", "title": "{title} is not paid yet" } }
                }
              }
            }
            """)!.AsObject())
    {
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "list": { "type": "string", "description": "The invoice list (default: all invoice lists of the workspace)." },
                "approvers": { "type": "array", "description": "Who approves: user names, group:Name or field:name." },
                "paymentDays": { "type": "number", "default": 30, "description": "How long to wait for the payment." }
              },
              "required": ["approvers"]
            }
            """)!.AsObject(),
    };
}

/// <summary>MCP tool <c>samples_invoices_pending</c> (API-09): invoices waiting for approval in a list the caller can read.</summary>
public sealed class PendingInvoicesTool(IListItemStore items) : IMcpTool
{
    public string Name => "samples_invoices_pending";

    public string Description => "Lists invoices of an invoice list that wait for approval, with their amounts.";

    public JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "Invoice list id.", true));

    public string? RequiredScope => "list.read";

    public bool IsReadOnly => true;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var (found, error) = await items.QueryAsync(arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"),
            new ListItemQuery("fields/status eq 'pendingApproval'", "fields/amount desc"), cancellationToken);
        return error is not null
            ? McpToolResult.Error(error)
            : McpToolResult.FromJson(new
            {
                invoices = found.Select(i => new { i.Id, title = i.Fields["title"]?.GetValue<string>(), amount = i.Fields["amount"]?.GetValue<decimal>() }),
            });
    }
}
