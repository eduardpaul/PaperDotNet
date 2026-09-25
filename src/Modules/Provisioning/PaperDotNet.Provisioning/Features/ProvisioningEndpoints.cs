using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using PaperDotNet.Api;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Provisioning.Features;

/// <summary>
/// Provisioning templates (PRV-01…03): export the tenant or a workspace as XML, apply a template
/// (idempotent, with parameters and a dry run) and download the XSD.
/// </summary>
internal static class ProvisioningEndpoints
{
    public const string XmlContentType = "application/xml";
    private const int MaxTemplateBytes = 10 * 1024 * 1024;
    private const string ParameterPrefix = "parameters[";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("provisioning", "Provisioning");
        group.MapGet("/schema", () => TypedResults.Text(TemplateReader.Schema, XmlContentType, Encoding.UTF8))
            .AllowAnonymous().WithName("GetTemplateSchema");
        group.MapGet("/export", ExportAsync).RequireScope(ProvisioningScopes.Read).WithName("ExportTemplate");
        group.MapPost("/apply", ApplyAsync).RequireScope(ProvisioningScopes.Manage).WithName("ApplyTemplate")
            .Accepts<string>(XmlContentType);
    }

    /// <summary>Exports the tenant (no <c>workspaceId</c>) or one workspace with what it depends on.</summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportAsync(
        Guid? workspaceId, TemplateEngine engine, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        if (workspaceId is { } id && await CheckWorkspaceAsync(workspaces, id, ct) is { } denied)
        {
            return denied;
        }

        try
        {
            var document = await engine.ExportAsync(workspaceId, ct);
            using var stream = new MemoryStream();
            await using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Indent = true, Encoding = new UTF8Encoding(false) }))
            {
                await document.SaveAsync(writer, ct);
            }

            return TypedResults.File(stream.ToArray(), XmlContentType, workspaceId is null ? "tenant-template.xml" : "workspace-template.xml");
        }
        catch (TemplateException ex)
        {
            return ApiErrors.Conflict("cannotExport", ex.Message);
        }
    }

    /// <summary>
    /// Applies the template in the body (XML). Query: <c>dryRun=true</c> lists the planned changes only;
    /// <c>workspaceId</c> applies a workspace template to that workspace; <c>parameters[Name]=value</c>
    /// sets parameters. Nothing is written when validation or the dry run fails.
    /// </summary>
    private static async Task<Results<Ok<TemplateResult>, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        HttpRequest request, bool? dryRun, Guid? workspaceId, TemplateEngine engine, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        if (request.ContentType is not { } contentType
            || !(contentType.StartsWith(XmlContentType, StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("text/xml", StringComparison.OrdinalIgnoreCase)))
        {
            return ApiErrors.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupportedMediaType", "Send the template as application/xml.");
        }

        if (workspaceId is { } id && await CheckWorkspaceAsync(workspaces, id, ct) is { } denied)
        {
            return denied;
        }

        if (request.ContentLength > MaxTemplateBytes)
        {
            return ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "templateTooLarge", "Templates may be at most 10 MB.");
        }

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, ct);
        if (body.Length > MaxTemplateBytes)
        {
            return ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "templateTooLarge", "Templates may be at most 10 MB.");
        }

        body.Position = 0;
        var warnings = new List<string>();
        List<string> errors;
        System.Xml.Linq.XDocument document;
        try
        {
            document = TemplateReader.Parse(body);
            errors = TemplateReader.Substitute(document.Root!, Parameters(request), warnings);
            if (errors.Count == 0)
            {
                errors = TemplateReader.Validate(document);
            }
        }
        catch (TemplateException ex)
        {
            return Invalid([ex.Message]);
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        TemplateResult plan;
        try
        {
            plan = await engine.ApplyAsync(document.Root!, workspaceId, dryRun: true, ct);
        }
        catch (TemplateException ex)
        {
            return Invalid([ex.Message]);
        }

        if (dryRun == true)
        {
            return TypedResults.Ok(plan with { Warnings = [.. warnings, .. plan.Warnings] });
        }

        try
        {
            var result = await engine.ApplyAsync(document.Root!, workspaceId, dryRun: false, ct);
            return TypedResults.Ok(result with { Warnings = [.. warnings, .. result.Warnings] });
        }
        catch (TemplateException ex)
        {
            return ApiErrors.Conflict("applyFailed", $"{ex.Message} Part of the template may have been applied; fix the problem and apply it again.");
        }
    }

    private static Dictionary<string, string> Parameters(HttpRequest request)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Query)
        {
            if (key.StartsWith(ParameterPrefix, StringComparison.Ordinal) && key.EndsWith(']') && key.Length > ParameterPrefix.Length + 1)
            {
                parameters[key[ParameterPrefix.Length..^1]] = value.ToString();
            }
        }

        return parameters;
    }

    private static async Task<ProblemHttpResult?> CheckWorkspaceAsync(IWorkspaceAccess workspaces, Guid workspaceId, CancellationToken ct) =>
        await workspaces.GetPermissionAsync(workspaceId, ct) switch
        {
            WorkspaceAccessLevel.None => ApiErrors.NotFound(),
            < WorkspaceAccessLevel.Manage => ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Managing the workspace is required."),
            _ => null,
        };

    private static ValidationProblem Invalid(IReadOnlyList<string> errors) =>
        ApiErrors.Validation(new Dictionary<string, string[]> { ["template"] = [.. errors] });
}
