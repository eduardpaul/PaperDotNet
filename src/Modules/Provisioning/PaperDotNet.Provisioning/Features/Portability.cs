using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Provisioning.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Provisioning.Features;

/// <summary>Options of export and import (<c>Provisioning</c> section).</summary>
public sealed class PortabilityOptions
{
    public const string Section = "Provisioning";

    /// <summary>Exported packages are kept this long for download.</summary>
    public int ExportRetentionDays { get; set; } = 7;

    /// <summary>Largest package accepted for import.</summary>
    public long MaxImportBytes { get; set; } = 4L * 1024 * 1024 * 1024;
}

public sealed record ExportRequest(Guid? WorkspaceId);

public sealed record ExportResponse(
    Guid Id, Guid OperationId, Guid? WorkspaceId, bool Ready, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("packageUrl")] string? PackageUrl);

public sealed record ImportResponse(Guid Id, Guid OperationId);

public sealed record ExportPayload(Guid PackageId);

public sealed record ImportPayload(Guid PackageId, bool DryRun, IReadOnlyDictionary<string, string> Parameters);

/// <summary>Import failed validation: the template's problems (the operation's result).</summary>
public sealed record ImportErrors(IReadOnlyList<string> Errors);

/// <summary>
/// Export and import of a workspace or the whole tenant with its content (PLT-13), as template packages (ADR-0028). Both
/// run as operations; this service is also used by the CLI (<c>paperdotnet export|import</c>).
/// </summary>
public sealed class PortabilityService(TemplateEngine engine)
{
    /// <summary>Writes the package of the tenant (or one workspace) to <paramref name="destination"/>.</summary>
    public async Task ExportAsync(Guid? workspaceId, Stream destination, CancellationToken ct)
    {
        await using var package = await PackageFile.ExportAsync(engine, workspaceId, ct);
        await package.CopyToAsync(destination, ct);
    }

    /// <summary>
    /// Applies a package (seekable stream) to the tenant, or one workspace template to <paramref name="workspaceId"/>:
    /// dry run first, then for real unless <paramref name="dryRun"/>. Returns the result, or the template's problems.
    /// </summary>
    public async Task<(TemplateResult? Result, IReadOnlyList<string> Errors)> ImportAsync(
        Stream package, Guid? workspaceId, bool dryRun, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
        await PackageFile.ApplyAsync(engine, package, parameters, workspaceId, dryRun, ct);
}

/// <summary>Writes an export's package to blob storage.</summary>
internal sealed class ExportOperation(ProvisioningDbContext db, PortabilityService portability, IBlobStore blobs, ITenantContext tenant)
    : OperationHandler<ExportPayload>
{
    public const string OperationType = "portability.export";

    public override string Type => OperationType;

    protected override async Task<object?> ExecuteAsync(ExportPayload payload, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var package = await db.Packages.FirstAsync(p => p.Id == payload.PackageId, cancellationToken);
        await progress.ReportAsync(5, cancellationToken);
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await portability.ExportAsync(package.WorkspaceId, file, cancellationToken);
            }

            await progress.ReportAsync(80, cancellationToken);
            var key = $"{tenant.TenantId:N}/portability/{package.Id:N}";
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                package.Size = file.Length;
                await blobs.WriteAsync(key, file, cancellationToken);
            }

            package.BlobKey = key;
            await db.SaveChangesAsync(cancellationToken);
            return new { packageId = package.Id, size = package.Size };
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Applies an uploaded package, then removes it.</summary>
internal sealed class ImportOperation(ProvisioningDbContext db, PortabilityService portability, IBlobStore blobs) : OperationHandler<ImportPayload>
{
    public const string OperationType = "portability.import";

    public override string Type => OperationType;

    protected override async Task<object?> ExecuteAsync(ImportPayload payload, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var package = await db.Packages.FirstAsync(p => p.Id == payload.PackageId, cancellationToken);
        try
        {
            // Zip needs a seekable stream: the blob is copied to a temporary file first.
            await using var content = await blobs.OpenReadAsync(package.BlobKey!, cancellationToken)
                ?? throw new InvalidOperationException("The uploaded package is gone.");
            await using var file = await PackageFile.SpoolAsync(content, long.MaxValue, cancellationToken);
            await progress.ReportAsync(10, cancellationToken);
            var (result, errors) = await portability.ImportAsync(file!, package.WorkspaceId, payload.DryRun, payload.Parameters, cancellationToken);
            return result is null ? new ImportErrors(errors) : result;
        }
        finally
        {
            await blobs.DeleteAsync(package.BlobKey!, CancellationToken.None);
            db.Packages.Remove(package);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}

/// <summary>Daily: deletes expired exports and leftover imports with their files.</summary>
internal sealed class PortabilityCleanupJob(ProvisioningDbContext db, IBlobStore blobs, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "provisioning.packageCleanup";
    public const string Schedule = "23 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var package in await db.Packages.Where(p => p.ExpiresAt < now).Take(500).ToListAsync(cancellationToken))
        {
            if (package.BlobKey is { } key)
            {
                await blobs.DeleteAsync(key, cancellationToken);
            }

            db.Packages.Remove(package);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Export and import (PLT-13): <c>POST /v1.0/portability/exports</c> starts an export of the tenant or a workspace
/// (download the package when ready); <c>POST /v1.0/portability/imports</c> applies an uploaded package. Tenant-wide
/// needs the template scopes (administrators); a workspace also needs Manage access to it.
/// </summary>
internal static class PortabilityEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("portability", "Portability");
        group.MapPost("/exports", StartExportAsync).RequireScope(ProvisioningScopes.Read).WithName("StartExport");
        group.MapGet("/exports", ListExportsAsync).RequireScope(ProvisioningScopes.Read).WithName("ListExports");
        group.MapGet("/exports/{id:guid}", GetExportAsync).RequireScope(ProvisioningScopes.Read).WithName("GetExport");
        group.MapGet("/exports/{id:guid}/package", DownloadAsync).RequireScope(ProvisioningScopes.Read).WithName("DownloadExport").ProducesBinary(ZipTemplatePackage.ContentType);
        group.MapDelete("/exports/{id:guid}", DeleteExportAsync).RequireScope(ProvisioningScopes.Read).WithName("DeleteExport");
        group.MapPost("/imports", StartImportAsync).RequireScope(ProvisioningScopes.Manage).WithName("StartImport")
            .Accepts<string>(ZipTemplatePackage.ContentType);
    }

    private static async Task<Results<Accepted<ExportResponse>, ProblemHttpResult>> StartExportAsync(
        ExportRequest? request, IWorkspaceAccess workspaces, ProvisioningDbContext db, IOperations operations, ICurrentUser user,
        IOptions<PortabilityOptions> options, TimeProvider time, HttpRequest http, CancellationToken ct)
    {
        if (request?.WorkspaceId is { } workspaceId && await CheckWorkspaceAsync(workspaces, workspaceId, ct) is { } denied)
        {
            return denied;
        }

        var now = time.GetUtcNow();
        var package = new PortabilityPackage
        {
            Id = Ids.New(),
            Kind = PackageKind.Export,
            WorkspaceId = request?.WorkspaceId,
            CreatedBy = user.UserId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, options.Value.ExportRetentionDays)),
        };
        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        package.OperationId = await operations.StartAsync(ExportOperation.OperationType, new ExportPayload(package.Id), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{package.OperationId}", Response(package));
    }

    /// <summary>The caller's exports that have not expired, newest first.</summary>
    private static async Task<Ok<List<ExportResponse>>> ListExportsAsync(ProvisioningDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var packages = await db.Packages.AsNoTracking()
            .Where(p => p.Kind == PackageKind.Export && p.CreatedBy == user.UserId)
            .OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        return TypedResults.Ok(packages.Select(Response).ToList());
    }

    private static async Task<Results<Ok<ExportResponse>, ProblemHttpResult>> GetExportAsync(Guid id, ProvisioningDbContext db, ICurrentUser user, CancellationToken ct) =>
        await FindAsync(db, user, id, ct) is { } package ? TypedResults.Ok(Response(package)) : ApiErrors.NotFound();

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadAsync(
        Guid id, ProvisioningDbContext db, ICurrentUser user, IBlobStore blobs, CancellationToken ct)
    {
        if (await FindAsync(db, user, id, ct) is not { BlobKey: { } key } package)
        {
            return ApiErrors.NotFound();
        }

        var stream = await blobs.OpenReadAsync(key, ct);
        return stream is null ? ApiErrors.NotFound()
            : TypedResults.File(stream, ZipTemplatePackage.ContentType, package.WorkspaceId is null ? "tenant-export.zip" : "workspace-export.zip");
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteExportAsync(
        Guid id, ProvisioningDbContext db, ICurrentUser user, IBlobStore blobs, CancellationToken ct)
    {
        if (await FindAsync(db, user, id, ct) is not { } package)
        {
            return ApiErrors.NotFound();
        }

        if (package.BlobKey is { } key)
        {
            await blobs.DeleteAsync(key, ct);
        }

        db.Packages.Remove(package);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Uploads a package (<c>application/zip</c>) and applies it as an operation. Query: <c>dryRun=true</c>,
    /// <c>workspaceId</c> (apply a workspace package to that workspace), <c>parameters[Name]=value</c>. The operation's
    /// result lists the changes and warnings, or the template's errors.
    /// </summary>
    private static async Task<Results<Accepted<ImportResponse>, ProblemHttpResult>> StartImportAsync(
        HttpRequest http, bool? dryRun, Guid? workspaceId, IWorkspaceAccess workspaces, ProvisioningDbContext db, IOperations operations, IBlobStore blobs,
        ICurrentUser user, ITenantContext tenant, IOptions<PortabilityOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (http.ContentType?.StartsWith(ZipTemplatePackage.ContentType, StringComparison.OrdinalIgnoreCase) != true)
        {
            return ApiErrors.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupportedMediaType", "Send the package as application/zip.");
        }

        if (workspaceId is { } id && await CheckWorkspaceAsync(workspaces, id, ct) is { } denied)
        {
            return denied;
        }

        var max = options.Value.MaxImportBytes;
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max;
        }

        if (http.ContentLength > max)
        {
            return TooLarge(max);
        }

        await using var file = await PackageFile.SpoolAsync(http.Body, max, ct);
        if (file is null)
        {
            return TooLarge(max);
        }

        var now = time.GetUtcNow();
        var package = new PortabilityPackage
        {
            Id = Ids.New(),
            Kind = PackageKind.Import,
            WorkspaceId = workspaceId,
            CreatedBy = user.UserId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
            Size = file.Length,
        };
        package.BlobKey = $"{tenant.TenantId:N}/portability/{package.Id:N}";
        await blobs.WriteAsync(package.BlobKey, file, ct);
        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        package.OperationId = await operations.StartAsync(
            ImportOperation.OperationType, new ImportPayload(package.Id, dryRun == true, ProvisioningEndpoints.Parameters(http)), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{package.OperationId}", new ImportResponse(package.Id, package.OperationId));
    }

    private static Task<PortabilityPackage?> FindAsync(ProvisioningDbContext db, ICurrentUser user, Guid id, CancellationToken ct) =>
        db.Packages.FirstOrDefaultAsync(p => p.Id == id && p.Kind == PackageKind.Export && p.CreatedBy == user.UserId, ct);

    private static ExportResponse Response(PortabilityPackage package) => new(
        package.Id, package.OperationId, package.WorkspaceId, package.BlobKey is not null, package.Size, package.CreatedAt, package.ExpiresAt,
        package.BlobKey is null ? null : $"{ApiRoutes.V1}/portability/exports/{package.Id}/package");

    private static ProblemHttpResult TooLarge(long max) =>
        ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "packageTooLarge", $"Packages may be at most {max} bytes.");

    private static async Task<ProblemHttpResult?> CheckWorkspaceAsync(IWorkspaceAccess workspaces, Guid workspaceId, CancellationToken ct) =>
        await workspaces.GetPermissionAsync(workspaceId, ct) switch
        {
            WorkspaceAccessLevel.None => ApiErrors.NotFound(),
            < WorkspaceAccessLevel.Manage => ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Managing the workspace is required."),
            _ => null,
        };
}
