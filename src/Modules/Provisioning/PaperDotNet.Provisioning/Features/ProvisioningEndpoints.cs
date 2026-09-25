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
/// Provisioning templates (PRV-01…04): export the tenant or a workspace as XML, or as a package (zip) with its
/// content; apply a template or package (idempotent, with parameters and a dry run); download the XSD.
/// </summary>
internal static class ProvisioningEndpoints
{
    public const string XmlContentType = "application/xml";
    private const int MaxTemplateBytes = 10 * 1024 * 1024;

    /// <summary>Packages sent to <c>apply</c> (larger ones go through the import operation, PLT-13).</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    private const string ParameterPrefix = "parameters[";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("provisioning", "Provisioning");
        group.MapGet("/schema", () => TypedResults.Text(TemplateReader.Schema, XmlContentType, Encoding.UTF8))
            .AllowAnonymous().WithName("GetTemplateSchema").ProducesBinary(XmlContentType);
        group.MapGet("/export", ExportAsync).RequireScope(ProvisioningScopes.Read).WithName("ExportTemplate").ProducesBinary(XmlContentType, ZipTemplatePackage.ContentType);
        group.MapPost("/apply", ApplyAsync).RequireScope(ProvisioningScopes.Manage).WithName("ApplyTemplate")
            .Accepts<string>(XmlContentType, ZipTemplatePackage.ContentType);
    }

    /// <summary>
    /// Exports the tenant (no <c>workspaceId</c>) or one workspace with what it depends on. With
    /// <c>includeContent=true</c> the result is a package (zip) that also holds the items and files (PRV-04).
    /// </summary>
    private static async Task<Results<FileContentHttpResult, FileStreamHttpResult, ProblemHttpResult>> ExportAsync(
        Guid? workspaceId, bool? includeContent, TemplateEngine engine, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        if (workspaceId is { } id && await CheckWorkspaceAsync(workspaces, id, ct) is { } denied)
        {
            return denied;
        }

        if (includeContent == true)
        {
            try
            {
                var file = await PackageFile.ExportAsync(engine, workspaceId, ct);
                return TypedResults.File(file, ZipTemplatePackage.ContentType, workspaceId is null ? "tenant-package.zip" : "workspace-package.zip");
            }
            catch (TemplateException ex)
            {
                return ApiErrors.Conflict("cannotExport", ex.Message);
            }
        }

        try
        {
            var document = await engine.ExportAsync(workspaceId, null, ct);
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
        var isPackage = request.ContentType?.StartsWith(ZipTemplatePackage.ContentType, StringComparison.OrdinalIgnoreCase) == true;
        if (!isPackage && (request.ContentType is not { } contentType
            || !(contentType.StartsWith(XmlContentType, StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("text/xml", StringComparison.OrdinalIgnoreCase))))
        {
            return ApiErrors.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupportedMediaType", "Send the template as application/xml, or a package as application/zip.");
        }

        if (workspaceId is { } id && await CheckWorkspaceAsync(workspaces, id, ct) is { } denied)
        {
            return denied;
        }

        if (isPackage)
        {
            return await ApplyPackageAsync(request, dryRun == true, workspaceId, engine, ct);
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

    /// <summary>A package (zip): spooled to a temporary file, then its template is read, checked and applied with it.</summary>
    private static async Task<Results<Ok<TemplateResult>, ValidationProblem, ProblemHttpResult>> ApplyPackageAsync(
        HttpRequest request, bool dryRun, Guid? workspaceId, TemplateEngine engine, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxPackageBytes;
        }

        if (request.ContentLength > MaxPackageBytes)
        {
            return ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "packageTooLarge", "Packages may be at most 512 MB here; use the import operation.");
        }

        await using var file = await PackageFile.SpoolAsync(request.Body, MaxPackageBytes, ct);
        if (file is null)
        {
            return ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "packageTooLarge", "Packages may be at most 512 MB here; use the import operation.");
        }

        try
        {
            var (result, errors) = await PackageFile.ApplyAsync(engine, file, Parameters(request), workspaceId, dryRun, ct);
            return result is null ? Invalid(errors) : TypedResults.Ok(result);
        }
        catch (TemplateException ex)
        {
            return ApiErrors.Conflict("applyFailed", $"{ex.Message} Part of the package may have been applied; fix the problem and apply it again.");
        }
    }

    internal static Dictionary<string, string> Parameters(HttpRequest request)
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
