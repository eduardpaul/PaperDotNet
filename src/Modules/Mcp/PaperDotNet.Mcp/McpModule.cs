using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Mcp.Features;

namespace PaperDotNet.Mcp;

public static class McpScopes
{
    public const string Use = "mcp.use";

    public static readonly ScopeDefinition[] All =
    [
        new(Use, "Let AI assistants use your data through MCP (tools still need their own scopes).", GrantedToMembers: true),
    ];
}

/// <summary>
/// MCP server (API-08/09) at <c>/v1.0/mcp</c> (Streamable HTTP, stateless). Serves every
/// <see cref="IMcpTool"/> of modules and enabled extensions; tools run as the caller, with the
/// caller's scopes and permissions.
/// </summary>
public sealed partial class McpModule : IModule
{
    public const string Route = "/v1.0/mcp";

    public string Name => "Mcp";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    internal static partial Regex ToolName();

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IMcpTool, WorkspacesTool>();
        services.AddScoped<IMcpTool, ListsTool>();
        services.AddScoped<IMcpTool, QueryItemsTool>();
        services.AddScoped<IMcpTool, GetItemTool>();
        services.AddScoped<IMcpTool, CreateItemTool>();
        services.AddScoped<IMcpTool, UpdateItemTool>();
        services.AddScopes(McpScopes.All);

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "PaperDotNet", Version = typeof(McpModule).Assembly.GetName().Version?.ToString() ?? "1.0" };
                options.ServerInstructions =
                    "PaperDotNet holds documents, tasks, events and other lists in workspaces. Start with list_workspaces and list_lists, " +
                    "then query_items or search. Everything runs with the permissions of the signed-in user.";
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp(Route).RequireScope(McpScopes.Use).ExcludeFromDescription();

    private static async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken ct)
    {
        var tools = new List<Tool>();
        foreach (var tool in await AvailableAsync(context.Services!, context.User, ct))
        {
            tools.Add(new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = tool.IsReadOnly, DestructiveHint = !tool.IsReadOnly && tool.Name.Contains("delete", StringComparison.Ordinal), OpenWorldHint = false },
            });
        }

        return new ListToolsResult { Tools = tools };
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var name = context.Params?.Name;
        var tool = (await AvailableAsync(context.Services!, context.User, ct)).FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return Result(McpToolResult.Error($"Unknown tool '{name}', or you may not use it."));
        }

        var arguments = new McpArguments(context.Params!.Arguments is { } args
            ? new Dictionary<string, JsonElement>(args)
            : new Dictionary<string, JsonElement>());
        try
        {
            return Result(await tool.CallAsync(arguments, ct));
        }
        catch (McpArgumentException ex)
        {
            return Result(McpToolResult.Error(ex.Message));
        }
    }

    /// <summary>Tools offered to the caller: available in the tenant and allowed by the caller's scopes.</summary>
    private static async Task<List<IMcpTool>> AvailableAsync(IServiceProvider services, System.Security.Claims.ClaimsPrincipal? user, CancellationToken ct)
    {
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var result = new List<IMcpTool>();
        foreach (var tool in services.GetServices<IMcpTool>())
        {
            if (!ToolName().IsMatch(tool.Name) || result.Any(t => t.Name == tool.Name) || !await tool.IsAvailableAsync(ct))
            {
                continue;
            }

            if (tool.RequiredScope is { } scope
                && (user is null || !(await authorization.AuthorizeAsync(user, ScopePolicyProvider.Prefix + scope)).Succeeded))
            {
                continue;
            }

            result.Add(tool);
        }

        return result;
    }

    private static CallToolResult Result(McpToolResult result) => new()
    {
        Content = [new TextContentBlock { Text = result.Text }],
        IsError = result.IsError,
        StructuredContent = result.Structured is null ? null : JsonSerializer.SerializeToElement(result.Structured),
    };
}
