using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Provisioning.Features;

/// <summary>
/// Template packages as temporary files (PRV-04, PLT-13): export writes the package to a file that is deleted when
/// its stream closes; apply spools the upload to a file, validates the template and applies it with the package.
/// </summary>
internal static class PackageFile
{
    /// <summary>A package of the tenant or workspace; the returned stream deletes its file when disposed.</summary>
    public static async Task<Stream> ExportAsync(TemplateEngine engine, Guid? workspaceId, CancellationToken ct)
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var package = ZipTemplatePackage.Create(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true)))
            {
                var template = await engine.ExportAsync(workspaceId, package, ct);
                await package.WriteTemplateAsync(template, ct);
            }

            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Copies <paramref name="content"/> to a temporary file (deleted on dispose); null when it exceeds <paramref name="maxBytes"/>.</summary>
    public static async Task<FileStream?> SpoolAsync(Stream content, long maxBytes, CancellationToken ct)
    {
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            if (file.Length + read > maxBytes)
            {
                await file.DisposeAsync();
                return null;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        file.Position = 0;
        return file;
    }

    /// <summary>
    /// Validates the package's template (parameters, schema) and applies it: always a dry run first, then the real run
    /// unless <paramref name="dryRun"/>. Returns validation errors instead of a result when the template is invalid.
    /// </summary>
    public static async Task<(TemplateResult? Result, List<string> Errors)> ApplyAsync(
        TemplateEngine engine, Stream file, IReadOnlyDictionary<string, string> parameters, Guid? workspaceId, bool dryRun, CancellationToken ct)
    {
        ZipTemplatePackage package;
        try
        {
            package = ZipTemplatePackage.Open(file, leaveOpen: true);
        }
        catch (TemplateException ex)
        {
            return (null, [ex.Message]);
        }

        await using var _ = package;
        var warnings = new List<string>();
        List<string> errors;
        System.Xml.Linq.XDocument document;
        try
        {
            document = package.ReadTemplate();
            errors = TemplateReader.Substitute(document.Root!, parameters, warnings);
            if (errors.Count == 0)
            {
                errors = TemplateReader.Validate(document);
            }
        }
        catch (TemplateException ex)
        {
            return (null, [ex.Message]);
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        TemplateResult result;
        try
        {
            result = await engine.ApplyAsync(document.Root!, workspaceId, dryRun: true, ct, package);
        }
        catch (TemplateException ex)
        {
            return (null, [ex.Message]);
        }

        if (!dryRun)
        {
            result = await engine.ApplyAsync(document.Root!, workspaceId, dryRun: false, ct, package);
        }

        return (result with { Warnings = [.. warnings, .. result.Warnings] }, []);
    }
}
