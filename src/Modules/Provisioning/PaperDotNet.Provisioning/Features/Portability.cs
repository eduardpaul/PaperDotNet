using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
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
    Guid Id, Guid OperationId, Guid? WorkspaceId, bool Ready, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? PackageUrl);

public sealed record ImportResponse(Guid Id, Guid OperationId);

public sealed record ExportPayload(Guid PackageId);

public sealed record ImportPayload(Guid PackageId, bool DryRun, Dictionary<string, string> Parameters);

public sealed record ExportResult(Guid PackageId, long Size);

/// <summary>Import failed validation: the template's problems (the operation's result).</summary>
public sealed record ImportErrors(IReadOnlyList<string> Errors);

/// <summary>
/// Export and import of a workspace or the whole tenant with its content (PLT-13), as template packages (ADR-0028). Both
/// run as operations, for the user who started them.
/// </summary>
public sealed class PortabilityService(TemplateEngine engine)
{
    /// <summary>Writes the package of the tenant (or one workspace) to <paramref name="destination"/>.</summary>
    public async Task ExportAsync(ChangeActor actor, Guid? workspaceId, Stream destination, CancellationToken ct)
    {
        await using var package = await PackageFile.ExportAsync(engine, actor, workspaceId, ct);
        await package.CopyToAsync(destination, ct);
    }

    /// <summary>
    /// Applies a package (seekable stream) to the tenant, or one workspace template to <paramref name="workspaceId"/>:
    /// dry run first, then for real unless <paramref name="dryRun"/>. Returns the result, or the template's problems.
    /// </summary>
    public async Task<(TemplateResult? Result, IReadOnlyList<string> Errors)> ImportAsync(
        ChangeActor actor, Stream package, Guid? workspaceId, bool dryRun, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
        await PackageFile.ApplyAsync(engine, actor, package, parameters, workspaceId, dryRun, ct);
}

/// <summary>Queries of the package table (precompiled: locals, one expression, explicit tenant).</summary>
internal static class Packages
{
    public static Task<PortabilityPackage?> FindAsync(ProvisioningDbContext database, Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var packageId = id;
        var ct = cancellationToken;
        return context.Packages.FirstOrDefaultAsync(p => p.TenantId == tenant && p.Id == packageId, ct);
    }

    /// <summary>An export of the user (tracked), or null.</summary>
    public static Task<PortabilityPackage?> FindExportAsync(ProvisioningDbContext database, Guid tenantId, Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var user = (Guid?)userId;
        var packageId = id;
        var kind = PackageKinds.Export;
        var ct = cancellationToken;
        return context.Packages.FirstOrDefaultAsync(p => p.TenantId == tenant && p.Id == packageId && p.Kind == kind && p.CreatedBy == user, ct);
    }

    public static Task<List<PortabilityPackage>> ExportsAsync(ProvisioningDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var user = (Guid?)userId;
        var kind = PackageKinds.Export;
        var ct = cancellationToken;
        return context.Packages.AsNoTracking()
            .Where(p => p.TenantId == tenant && p.Kind == kind && p.CreatedBy == user)
            .OrderByDescending(p => p.CreatedAtUnixMs).Take(100).ToListAsync(ct);
    }

    public static Task<List<PortabilityPackage>> ExpiredAsync(ProvisioningDbContext database, Guid tenantId, long nowUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var now = nowUnixMs;
        var ct = cancellationToken;
        return context.Packages.Where(p => p.TenantId == tenant && p.ExpiresAtUnixMs < now).Take(500).ToListAsync(ct);
    }

    public static string BlobKey(Guid tenantId, Guid packageId) => $"{tenantId:N}/portability/{packageId:N}";
}

/// <summary>Writes an export's package to blob storage.</summary>
internal sealed class ExportOperation(ProvisioningDbContext db, PortabilityService portability, IBlobStore blobs) : OperationHandler<ExportPayload>
{
    public const string OperationType = "portability.export";

    public override string Type => OperationType;

    protected override JsonTypeInfo<ExportPayload> PayloadJson => ProvisioningJson.Default.ExportPayload;

    protected override async Task<JsonNode?> ExecuteAsync(ExportPayload payload, OperationContext context, CancellationToken cancellationToken)
    {
        var tenantId = context.Actor.TenantId;
        var package = await Packages.FindAsync(db, tenantId, payload.PackageId, cancellationToken)
            ?? throw new InvalidOperationException("The export was deleted.");
        await context.Progress.ReportAsync(5, cancellationToken);
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await portability.ExportAsync(context.Actor, package.WorkspaceId, file, cancellationToken);
            }

            await context.Progress.ReportAsync(80, cancellationToken);
            var key = Packages.BlobKey(tenantId, package.Id);
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                package.Size = file.Length;
                await blobs.WriteAsync(key, file, cancellationToken);
            }

            package.BlobKey = key;
            await db.SaveChangesAsync(cancellationToken);
            return JsonSerializer.SerializeToNode(new ExportResult(package.Id, package.Size), ProvisioningJson.Default.ExportResult);
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

    protected override JsonTypeInfo<ImportPayload> PayloadJson => ProvisioningJson.Default.ImportPayload;

    protected override async Task<JsonNode?> ExecuteAsync(ImportPayload payload, OperationContext context, CancellationToken cancellationToken)
    {
        var package = await Packages.FindAsync(db, context.Actor.TenantId, payload.PackageId, cancellationToken)
            ?? throw new InvalidOperationException("The uploaded package is gone.");
        try
        {
            // Zip needs a seekable stream: the blob is copied to a temporary file first.
            await using var content = await blobs.OpenReadAsync(package.BlobKey!, cancellationToken)
                ?? throw new InvalidOperationException("The uploaded package is gone.");
            await using var file = await PackageFile.SpoolAsync(content, long.MaxValue, cancellationToken);
            await context.Progress.ReportAsync(10, cancellationToken);
            var (result, errors) = await portability.ImportAsync(context.Actor, file!, package.WorkspaceId, payload.DryRun, payload.Parameters, cancellationToken);
            return result is null
                ? JsonSerializer.SerializeToNode(new ImportErrors(errors), ProvisioningJson.Default.ImportErrors)
                : JsonSerializer.SerializeToNode(result, ProvisioningJson.Default.TemplateResult);
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

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        foreach (var package in await Packages.ExpiredAsync(db, tenantId, time.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken))
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
        var group = endpoints.MapGroup("/v1.0/portability").WithTags("Portability");
        group.MapPost("/exports", StartExportAsync).RequireScope(ProvisioningScopes.Read).WithName("StartExport");
        group.MapGet("/exports", ListExportsAsync).RequireScope(ProvisioningScopes.Read).WithName("ListExports");
        group.MapGet("/exports/{id:guid}", GetExportAsync).RequireScope(ProvisioningScopes.Read).WithName("GetExport");
        group.MapGet("/exports/{id:guid}/package", DownloadAsync).RequireScope(ProvisioningScopes.Read).WithName("DownloadExport")
            .Produces(StatusCodes.Status200OK, contentType: ZipTemplatePackage.ContentType);
        group.MapDelete("/exports/{id:guid}", DeleteExportAsync).RequireScope(ProvisioningScopes.Read).WithName("DeleteExport");
        group.MapPost("/imports", StartImportAsync).RequireScope(ProvisioningScopes.Manage).WithName("StartImport")
            .Accepts<string>(ZipTemplatePackage.ContentType)
            .WithDescription("Uploads a package (application/zip) and applies it as an operation. Query: dryRun=true, workspaceId (apply a workspace "
                + "package to that workspace), parameters[Name]=value. The operation's result lists the changes and warnings, or the template's errors.");
    }

    private static async Task<Results<Accepted<ExportResponse>, ProblemHttpResult>> StartExportAsync(
        ExportRequest? request, Caller caller, IWorkspaceAccess workspaces, ProvisioningDbContext db, IOperations operations,
        IOptions<PortabilityOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (request?.WorkspaceId is { } workspaceId && await CheckWorkspaceAsync(workspaces, caller, workspaceId, ct) is { } denied)
        {
            return denied;
        }

        var now = time.GetUtcNow();
        var expires = now.AddDays(Math.Max(1, options.Value.ExportRetentionDays));
        var package = new PortabilityPackage
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            Kind = PackageKinds.Export,
            WorkspaceId = request?.WorkspaceId,
            CreatedBy = caller.UserId,
            CreatedAt = now,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAt = expires,
            ExpiresAtUnixMs = expires.ToUnixTimeMilliseconds(),
        };
        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        package.OperationId = await operations.StartAsync(caller.Actor, ExportOperation.OperationType, new ExportPayload(package.Id), ProvisioningJson.Default.ExportPayload, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/v1.0/operations/{package.OperationId}", Response(package));
    }

    /// <summary>The caller's exports that have not been deleted, newest first.</summary>
    private static async Task<Ok<List<ExportResponse>>> ListExportsAsync(Caller caller, ProvisioningDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await Packages.ExportsAsync(db, caller.TenantId, caller.UserId, ct)).Select(Response).ToList());

    private static async Task<Results<Ok<ExportResponse>, ProblemHttpResult>> GetExportAsync(Guid id, Caller caller, ProvisioningDbContext db, CancellationToken ct) =>
        await Packages.FindExportAsync(db, caller.TenantId, caller.UserId, id, ct) is { } package ? TypedResults.Ok(Response(package)) : ApiErrors.NotFound();

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadAsync(
        Guid id, Caller caller, ProvisioningDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        if (await Packages.FindExportAsync(db, caller.TenantId, caller.UserId, id, ct) is not { BlobKey: { } key } package)
        {
            return ApiErrors.NotFound();
        }

        var stream = await blobs.OpenReadAsync(key, ct);
        return stream is null ? ApiErrors.NotFound()
            : TypedResults.File(stream, ZipTemplatePackage.ContentType, package.WorkspaceId is null ? "tenant-export.zip" : "workspace-export.zip");
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteExportAsync(
        Guid id, Caller caller, ProvisioningDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        if (await Packages.FindExportAsync(db, caller.TenantId, caller.UserId, id, ct) is not { } package)
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

    /// <summary>Uploads a package (<c>application/zip</c>) and applies it as an operation.</summary>
    private static async Task<Results<Accepted<ImportResponse>, ProblemHttpResult>> StartImportAsync(
        HttpRequest http, bool? dryRun, Guid? workspaceId, Caller caller, IWorkspaceAccess workspaces, ProvisioningDbContext db, IOperations operations,
        IBlobStore blobs, IOptions<PortabilityOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (http.ContentType?.StartsWith(ZipTemplatePackage.ContentType, StringComparison.OrdinalIgnoreCase) != true)
        {
            return ApiErrors.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupportedMediaType", "Send the package as application/zip.");
        }

        if (workspaceId is { } id && await CheckWorkspaceAsync(workspaces, caller, id, ct) is { } denied)
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
            TenantId = caller.TenantId,
            Kind = PackageKinds.Import,
            WorkspaceId = workspaceId,
            CreatedBy = caller.UserId,
            CreatedAt = now,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAt = now.AddDays(1),
            ExpiresAtUnixMs = now.AddDays(1).ToUnixTimeMilliseconds(),
            Size = file.Length,
        };
        package.BlobKey = Packages.BlobKey(caller.TenantId, package.Id);
        await blobs.WriteAsync(package.BlobKey, file, ct);
        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        package.OperationId = await operations.StartAsync(
            caller.Actor, ImportOperation.OperationType, new ImportPayload(package.Id, dryRun == true, ProvisioningEndpoints.Parameters(http)),
            ProvisioningJson.Default.ImportPayload, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/v1.0/operations/{package.OperationId}", new ImportResponse(package.Id, package.OperationId));
    }

    private static ExportResponse Response(PortabilityPackage package) => new(
        package.Id, package.OperationId, package.WorkspaceId, package.BlobKey is not null, package.Size, package.CreatedAt, package.ExpiresAt,
        package.BlobKey is null ? null : $"/v1.0/portability/exports/{package.Id}/package");

    private static ProblemHttpResult TooLarge(long max) =>
        ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "packageTooLarge", $"Packages may be at most {max} bytes.");

    private static async Task<ProblemHttpResult?> CheckWorkspaceAsync(IWorkspaceAccess workspaces, Caller caller, Guid workspaceId, CancellationToken ct) =>
        await workspaces.GetPermissionAsync(caller.TenantId, caller.UserId, workspaceId, ct) switch
        {
            WorkspaceAccessLevel.None => ApiErrors.NotFound(),
            < WorkspaceAccessLevel.Manage => ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Managing the workspace is required."),
            _ => null,
        };
}
