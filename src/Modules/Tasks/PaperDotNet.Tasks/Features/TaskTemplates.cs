using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Tasks.Features;

/// <summary>The task content type and the Tasks list template (TSK-01, TSK-04), registered like built-in ones.</summary>
public static class TaskTemplates
{
    public const string ContentTypeKey = "task";
    public const string ListTemplateKey = "tasks";
    public const string Completed = "completed";

    private static FieldDefinition Field(string name, string displayName, string type, Action<FieldDefinition>? configure = null)
    {
        var field = new FieldDefinition { Name = name, DisplayName = displayName, Type = type };
        configure?.Invoke(field);
        return field;
    }

    public static readonly ContentTypeTemplate ContentType = new(ContentTypeKey, "Task", "Something to do, with status, priority, dates and assignees.",
    [
        Field("status", "Status", "choice", f => { f.Choices = ["notStarted", "inProgress", Completed]; f.DefaultValue = "\"notStarted\""; }),
        Field("priority", "Priority", "choice", f => { f.Choices = ["low", "normal", "high"]; f.DefaultValue = "\"normal\""; }),
        Field("startDate", "Start date", "date"),
        Field("dueDate", "Due date", "date"),
        Field("assignedTo", "Assigned to", "person", f => f.AllowMultiple = true),
        Field("percentComplete", "% complete", "number", f => { f.Minimum = 0; f.Maximum = 100; }),
        Field("description", "Description", "note"),
    ]);

    public static readonly ListTemplateDefinition List = new(ListTemplateKey, "Tasks", "Tasks with status, priority, due dates and a board.", [ContentTypeKey],
    [
        new ViewTemplate("Active tasks", ["title", "status", "priority", "dueDate", "assignedTo"], $"fields/status ne '{Completed}'", "fields/dueDate", IsDefault: true),
        new ViewTemplate("Board", ["title", "priority", "dueDate", "assignedTo"], GroupBy: "status", Layout: "board"),
        new ViewTemplate("Completed", ["title", "dueDate", "assignedTo"], $"fields/status eq '{Completed}'"),
    ]);
}
