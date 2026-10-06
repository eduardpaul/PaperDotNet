using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Search.Features;

public sealed record SearchPolicyResponse(Guid ContainerId, bool Included)
{
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record SearchPolicyRequest(bool Included);

public sealed record ItemSearchStatusResponse(string State, bool Included, bool Indexed, string? CurrentRevision,
    string? IndexedRevision, DateTimeOffset? IndexedAt, int Chunks, bool Truncated, string? EmbeddingModel, string EmbeddingState, string ContentState, WorkflowRunInfo? Run);

public sealed record SearchPolicyChanged : IntegrationEvent
{
    public required Guid WorkspaceId { get; init; }
    public required Guid ContainerId { get; init; }
}

internal sealed class SearchPolicy(SearchDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user)
{
    public async Task<SearchContainerPolicy> SetAsync(Guid workspaceId, Guid containerId, bool included, CancellationToken ct)
    {
        var policy = await db.ContainerPolicies.FirstOrDefaultAsync(p => p.Id == containerId, ct);
        if (policy is null)
        {
            policy = new SearchContainerPolicy { Id = containerId };
            db.ContainerPolicies.Add(policy);
        }
        policy.Included = included;
        await outbox.SaveChangesAsync(db, [new SearchPolicyChanged
        {
            TenantId = tenant.TenantId!.Value, TenantIdentifier = tenant.TenantIdentifier!, UserId = user.UserId,
            WorkspaceId = workspaceId, ContainerId = containerId,
        }], cancellationToken: ct);
        return policy;
    }
}

internal sealed class SearchPolicySubscriber(IWorkflowTriggers triggers) : IEventSubscriber<SearchPolicyChanged>
{
    public Task HandleAsync(SearchPolicyChanged change, CancellationToken ct) =>
        triggers.RaiseAsync(SearchTriggers.ContainerChanged, change.WorkspaceId, null,
            new JsonObject { ["containerId"] = change.ContainerId.ToString() }, change, ct);
}

internal static class SearchPolicyEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var lists = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}", "Search indexing");
        lists.MapGet("/searchSettings", GetPolicyAsync).RequireScope(SearchScopes.Read).WithName("GetListSearchSettings");
        lists.MapPut("/searchSettings", SetPolicyAsync).RequireScope(SearchScopes.Write).WithName("SetListSearchSettings");
        lists.MapGet("/items/{itemId:guid}/searchIndex", GetStatusAsync).RequireScope(SearchScopes.Read).WithName("GetItemSearchStatus");
    }

    private static async Task<Results<Ok<SearchPolicyResponse>, ProblemHttpResult>> GetPolicyAsync(
        Guid workspaceId, Guid listId, IListItemStore items, SearchDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await items.GetListAsync(workspaceId, listId, ct) is null) { return ApiErrors.NotFound(); }
        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == listId, ct);
        ETags.Set(response, policy?.Version ?? 0);
        return TypedResults.Ok(new SearchPolicyResponse(listId, policy?.Included ?? true) { ETag = ETags.From(policy?.Version ?? 0) });
    }

    private static async Task<Results<Ok<SearchPolicyResponse>, ProblemHttpResult>> SetPolicyAsync(
        Guid workspaceId, Guid listId, SearchPolicyRequest request, IListItemStore items, SearchDbContext db, SearchPolicy policies,
        IWorkspaceAccess workspaces, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await items.GetListAsync(workspaceId, listId, ct) is null) { return ApiErrors.NotFound(); }
        if (await workspaces.GetPermissionAsync(workspaceId, ct) < WorkspaceAccessLevel.Manage) { return ApiErrors.Problem(403, "forbidden", "Manage access is required."); }
        var existing = await db.ContainerPolicies.FirstOrDefaultAsync(p => p.Id == listId, ct);
        if (!ETags.TryGetIfMatch(http, out var expected)) { return ApiErrors.Problem(428, "preconditionRequired", "If-Match is required."); }
        if (expected != (existing?.Version ?? 0)) { return ApiErrors.Problem(412, "versionMismatch", "The search settings changed. Refresh and try again."); }
        try
        {
            var policy = await policies.SetAsync(workspaceId, listId, request.Included, ct);
            ETags.Set(response, policy.Version);
            return TypedResults.Ok(new SearchPolicyResponse(listId, policy.Included) { ETag = ETags.From(policy.Version) });
        }
        catch (DbUpdateConcurrencyException) { return ApiErrors.PreconditionFailed(); }
        catch (DbUpdateException) when (existing is null)
        {
            db.ChangeTracker.Clear();
            if (await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == listId, ct)) { return ApiErrors.PreconditionFailed(); }
            throw;
        }
    }

    private static async Task<Results<Ok<ItemSearchStatusResponse>, ProblemHttpResult>> GetStatusAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, SearchInput input, SearchDbContext db, IWorkflowDirectory workflows, EmbeddingModel embeddings, ISearchStore store, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null) { return ApiErrors.NotFound(); }
        var included = !await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == listId && !p.Included, ct);
        var run = await workflows.GetLatestRunAsync(workspaceId, itemId, SearchWorkflows.Index, ct);
        var publication = await db.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.Id == itemId, ct);
        var source = await input.ReadAsync(itemId, ct);
        var revision = source is null ? null : SearchInput.Revision(source);
        // The pipeline that fills the search.index role now (the built-in, a copy or a replacement) and its version.
        var pipeline = await workflows.GetRoleWorkflowAsync(workspaceId, SearchWorkflows.Index, listId, ct) is { } role
            ? SearchWorkflows.Pipeline(role.WorkflowId, role.Version)
            : null;
        var indexed = included && publication?.Revision is not null;
        // Current only when the text and the pipeline both match what was published.
        var current = indexed && publication!.Revision == revision && (pipeline is null || publication.Settings is null || publication.Settings == pipeline);
        var state = !included ? "excluded" : run?.Status is "running" or "waiting" ? run.Status
            : run?.Status == "failed" ? "failed" : !indexed ? "notIndexed" : current ? "indexed" : "stale";
        return TypedResults.Ok(new ItemSearchStatusResponse(state, included, indexed, revision, publication?.Revision,
            publication?.PublishedAt, publication?.Chunks ?? 0, publication?.Truncated ?? false, publication?.EmbeddingModel,
            !embeddings.Enabled ? "notConfigured" : !store.Capabilities.HasFlag(SearchStoreCapabilities.Vector) ? "notSupported"
                : publication?.EmbeddingModel == embeddings.ModelKey ? "ready" : "pending",
            source?.ContentRevision is null ? "notApplicable" : !source.ContentReady ? "pending" : source.Pages.Any(p => !string.IsNullOrWhiteSpace(p)) ? "ready" : "noText", run));
    }
}

internal sealed class SearchPolicyTemplateHandler(SearchDbContext db, SearchPolicy policies) : ITemplateHandler
{
    public XName Element => XName.Get("SearchSettings", "urn:paperdotnet:search:1");
    public TemplateLevel Level => TemplateLevel.List;
    public int Order => 105;
    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken ct)
    {
        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == context.ListId, ct);
        return policy is null ? null : new XElement(Element, new XAttribute("Included", policy.Included));
    }
    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken ct)
    {
        var value = section.Attribute("Included")?.Value;
        if (!bool.TryParse(value, out var included)) { throw new TemplateException("Included must be true or false.", section); }
        var policy = context.IsPlanned ? null : await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == context.ListId, ct);
        if (policy?.Included == included) { return; }
        if (policy is null) { context.Created(TemplateKinds.Settings, $"{context.ListName}: search settings"); }
        else { context.Updated(TemplateKinds.Settings, $"{context.ListName}: search settings"); }
        if (!context.DryRun) { await policies.SetAsync(context.WorkspaceId!.Value, context.ListId!.Value, included, ct); }
    }
}
