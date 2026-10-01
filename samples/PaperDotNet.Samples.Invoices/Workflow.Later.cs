using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Samples.Invoices;

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
