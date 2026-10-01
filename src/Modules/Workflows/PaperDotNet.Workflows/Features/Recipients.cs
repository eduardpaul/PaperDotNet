using System.Text.Json.Nodes;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// People inputs of activities (<see cref="IWorkflowRecipients"/>), read against the run's item: user names,
/// <c>group:Name</c> (members, groups inside groups included), <c>field:name</c> (person values of the item),
/// <c>creator</c> (of the item) and <c>actor</c> (who started the run).
/// </summary>
internal sealed class WorkflowRecipientResolver(IUserDirectory users, IListItemStore items) : IWorkflowRecipients
{
    public async Task<WorkflowRecipients> ResolveAsync(IEnumerable<string> people, WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var tenant = context.TenantId;
        var item = context.ListId is { } listId && context.ItemId is { } itemId
            ? await items.AsSystem(context.Actor).GetAsync(context.WorkspaceId, listId, itemId, cancellationToken)
            : null;
        var result = new List<Guid>();
        var unknown = new List<string>();
        foreach (var spec in people.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (spec == "creator")
            {
                result.AddRange(item?.CreatedBy is { } creator ? [creator] : []);
            }
            else if (spec == "actor")
            {
                result.AddRange(context.StartedBy is { } user ? [user] : []);
            }
            else if (spec.StartsWith("field:", StringComparison.Ordinal))
            {
                var value = item?.Fields[spec["field:".Length..]];
                IEnumerable<JsonNode?> values = value is JsonArray array ? array : [value];
                result.AddRange(values.Select(v => v is JsonValue s && s.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty));
            }
            else if (spec.StartsWith("group:", StringComparison.Ordinal))
            {
                if (await users.FindGroupAsync(tenant, spec["group:".Length..], cancellationToken) is { } group)
                {
                    result.AddRange(await users.GetGroupMembersAsync(tenant, group, cancellationToken));
                }
                else
                {
                    unknown.Add(spec);
                }
            }
            else if (await users.FindUserAsync(tenant, spec, cancellationToken) is { } user)
            {
                result.Add(user);
            }
            else
            {
                unknown.Add(spec);
            }
        }

        return new WorkflowRecipients([.. result.Distinct()], unknown);
    }
}
