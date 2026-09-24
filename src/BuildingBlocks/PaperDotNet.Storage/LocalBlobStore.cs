using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Blob storage on the local file system under <c>{Storage:DataPath}/blobs</c> (default <c>data/blobs</c>),
    /// next to the SQLite database: one volume holds everything.
    /// </summary>
    public static IServiceCollection AddPaperDotNetStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var dataPath = configuration["Storage:DataPath"] is { Length: > 0 } path ? path : "data";
        services.AddSingleton<IBlobStore>(new LocalBlobStore(Path.Combine(dataPath, "blobs")));
        services.AddHealthChecks().AddCheck<BlobStoreHealthCheck>("storage", tags: ["ready"]);
        return services;
    }
}

/// <summary>Stores each key as a file; writes go to a temporary file first and are moved into place.</summary>
public sealed partial class LocalBlobStore : IBlobStore
{
    private const string TempFolder = ".tmp";

    public LocalBlobStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(Root, TempFolder));
    }

    public string Root { get; }

    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(Root, TempFolder, Guid.CreateVersion7().ToString("N"));
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(File.Exists(PathFor(key)));

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    /// <summary>Checks the key (no traversal, no absolute paths) and maps it below the root.</summary>
    internal string PathFor(string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"Invalid blob key '{key}'.", nameof(key));
        }

        return Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    [GeneratedRegex("^[a-z0-9_-]+(/[a-z0-9_-]+)*$")]
    private static partial Regex KeyPattern();
}

internal sealed class BlobStoreHealthCheck(IBlobStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        const string key = "health/probe";
        try
        {
            using var probe = new MemoryStream([1]);
            await store.WriteAsync(key, probe, cancellationToken);
            await store.DeleteAsync(key, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (IOException ex)
        {
            return HealthCheckResult.Unhealthy("The blob storage is not writable.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return HealthCheckResult.Unhealthy("The blob storage is not writable.", ex);
        }
    }
}
