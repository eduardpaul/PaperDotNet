using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Samples.Invoices;

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
