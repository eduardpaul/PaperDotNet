using System.Text.Json.Nodes;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Notifications.Features;

/// <summary>
/// <c>notify</c>: sends a notification (<c>to</c>, <c>title</c>, <c>body</c>) linked to the run's item. Notifications add it to
/// workflows through the SDK, like an extension.
/// </summary>
internal sealed class NotifyActivity(IWorkflowRecipients recipients, INotificationSender sender) : IWorkflowActivity
{
    public string Key => "notify";

    public string Description => "Notifies people: { \"to\": [\"alice\", \"group:Accountants\", \"field:owner\", \"creator\"], \"title\": \"{title} was filed\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        ActivityInputs.Required(inputs, "title").Concat(ActivityInputs.Texts(inputs, "to") is { Count: > 0 } ? [] : ["to is required."]);

    public JsonObject? InputSchema => ActivitySchemas.Of(["to", "title"],
        ("to", ActivitySchemas.People("Recipients.")), ("title", ActivitySchemas.Text("Title (template).")), ("body", ActivitySchemas.Text("Text (template).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("recipients", ActivitySchemas.Number("How many people were notified.")), ("unknown", ActivitySchemas.Texts("Recipients that were not found.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var (users, unknown) = await recipients.ResolveAsync(ActivityInputs.Texts(context.Inputs, "to")!, context, cancellationToken);
        var title = await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "title")!, cancellationToken);
        var body = ActivityInputs.Text(context.Inputs, "body") is { } text ? await context.ExpandAsync(text, cancellationToken) : null;
        var link = context.Item is { } i ? new NotificationLink(i.WorkspaceId, i.ListId, i.ItemId) : null;
        var sent = await sender.SendAsync(new NotificationMessage(NotificationTypes.Workflow, title, body, link, context.ExecutionKey), users, cancellationToken);
        return WorkflowActivityResult.Ok(new JsonObject
        {
            ["recipients"] = sent,
            ["unknown"] = unknown.Count == 0 ? null : new JsonArray([.. unknown.Select(u => JsonValue.Create(u))]),
        });
    }
}
