using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PaperDotNet.Host;

/// <summary>Documents bearer authentication (JWT access tokens and <c>pdn_</c> API tokens).</summary>
internal sealed class BearerSecurityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "PaperDotNet API";
        document.Info.Description = "Graph-style REST API for documents, tasks, calendar and lists.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Access token from POST /v1.0/auth/token, or a personal API token (pdn_…).",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] });
        return Task.CompletedTask;
    }
}
