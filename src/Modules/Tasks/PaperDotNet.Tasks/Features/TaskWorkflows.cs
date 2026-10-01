using System.Globalization;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Tasks.Features;

/// <summary>
/// <c>task.create</c>: creates a task in a task list of the workspace (<c>list</c>, <c>title</c>, <c>assignedTo</c>,
/// <c>dueInDays</c>, <c>priority</c>, <c>description</c>). Tasks add it to workflows through the SDK, like an extension.
/// Activities are singletons: scoped services come from the context.
/// </summary>
internal sealed class TaskCreateActivity(TimeProvider time) : IWorkflowActivity
{
    public string Key => "task.create";

    public string Description => "Creates a task: { \"list\": \"Tasks\", \"title\": \"Check {title}\", \"assignedTo\": [\"field:owner\"], \"dueInDays\": 3 }.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list", "title");

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var store = context.Services.GetRequiredService<IListItemStore>().AsSystem(context.Actor);
        var listName = await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "list") ?? "", cancellationToken);
        var list = (await store.GetListsAsync(context.WorkspaceId, null, cancellationToken)).FirstOrDefault(l => l.Name == listName);
        if (list is null || !list.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey))
        {
            return WorkflowActivityResult.Fail($"The task list '{listName}' does not exist in the workspace.");
        }

        var fields = new JsonObject { ["title"] = await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "title") ?? "", cancellationToken) };
        if (ActivityInputs.Texts(context.Inputs, "assignedTo") is { Count: > 0 } people)
        {
            var assignees = await context.Services.GetRequiredService<IWorkflowRecipients>().ResolveAsync(people, context, cancellationToken);
            fields["assignedTo"] = new JsonArray([.. assignees.Users.Select(u => JsonValue.Create(u.ToString()))]);
        }

        if (ActivityInputs.Number(context.Inputs, "dueInDays") is { } days)
        {
            fields["dueDate"] = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(days)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        foreach (var name in new[] { "priority", "description" })
        {
            if (ActivityInputs.Text(context.Inputs, name) is { } value)
            {
                fields[name] = await context.ExpandAsync(value, cancellationToken);
            }
        }

        // The execution id as the task's id: a repeated step finds the task it created before.
        var created = await store.CreateAsync(context.WorkspaceId, list.Id, context.ExecutionId, fields, null, cancellationToken);
        return created.Succeeded
            ? WorkflowActivityResult.Ok(new JsonObject { ["taskId"] = created.Item!.Id.ToString() })
            : WorkflowActivityResult.Fail(created.Describe());
    }
}
