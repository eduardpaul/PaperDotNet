using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Provisioning.Features;

/// <summary>
/// A template package as a zip file (PRV-04): <c>template.xml</c>, JSON documents and files named by their SHA-256.
/// Entries are only looked up by name, never extracted to disk; sizes are checked before anything is read.
/// </summary>
internal sealed class ZipTemplatePackage : ITemplatePackage, IAsyncDisposable
{
    public const string TemplateEntry = "template.xml";
    public const string ContentType = "application/zip";
    public const string FilesFolder = "files/";

    /// <summary>JSON documents and the template are read into memory, so they have a limit; files are streamed.</summary>
    private const long MaxDocumentBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ZipArchive _zip;
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);

    private ZipTemplatePackage(Stream stream, ZipArchiveMode mode, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _zip = new ZipArchive(stream, mode, leaveOpen: true);
    }

    /// <summary>A new package written to <paramref name="stream"/> (disposed with the package).</summary>
    public static ZipTemplatePackage Create(Stream stream) => new(stream, ZipArchiveMode.Create);

    /// <summary>Reads a package; throws <see cref="TemplateException"/> when it is not a zip with a template.</summary>
    public static ZipTemplatePackage Open(Stream stream, bool leaveOpen = false)
    {
        try
        {
            var package = new ZipTemplatePackage(stream, ZipArchiveMode.Read, leaveOpen);
            return package._zip.GetEntry(TemplateEntry) is null
                ? throw new TemplateException($"The package has no {TemplateEntry}.")
                : package;
        }
        catch (InvalidDataException ex)
        {
            throw new TemplateException($"The package is not a valid zip file: {ex.Message}");
        }
    }

    /// <summary>The package's <c>template.xml</c>, parsed safely.</summary>
    public XDocument ReadTemplate()
    {
        using var stream = OpenLimited(_zip.GetEntry(TemplateEntry)!);
        return TemplateReader.Parse(stream);
    }

    public async Task WriteTemplateAsync(XDocument template, CancellationToken cancellationToken)
    {
        await using var entry = await _zip.CreateEntry(TemplateEntry, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await using var writer = XmlWriter.Create(entry, new XmlWriterSettings { Async = true, Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        await template.SaveAsync(writer, cancellationToken);
    }

    public async Task WriteJsonAsync(string path, JsonNode content, CancellationToken cancellationToken)
    {
        await using var entry = await _zip.CreateEntry(path, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await JsonSerializer.SerializeAsync(entry, content, Json, cancellationToken);
    }

    public async Task<string> AddFileAsync(Stream content, CancellationToken cancellationToken)
    {
        // Hash while spooling to a temporary file: the entry is named by the hash and written once.
        var temp = Path.GetTempFileName();
        try
        {
            string hash;
            await using (var spool = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                var path = FilesFolder + hash;
                if (_files.Add(path))
                {
                    spool.Position = 0;
                    await using var entry = await _zip.CreateEntry(path, CompressionLevel.Fastest).OpenAsync(cancellationToken);
                    await spool.CopyToAsync(entry, cancellationToken);
                }

                return path;
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task<JsonNode?> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        if (_zip.GetEntry(path) is not { } entry)
        {
            return null;
        }

        await using var stream = OpenLimited(entry);
        try
        {
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new TemplateException($"{path} in the package is not valid JSON: {ex.Message}");
        }
    }

    public Stream? OpenFile(string path) => _zip.GetEntry(path)?.Open();

    public async ValueTask DisposeAsync()
    {
        await _zip.DisposeAsync();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync();
        }
    }

    private static Stream OpenLimited(ZipArchiveEntry entry) =>
        entry.Length > MaxDocumentBytes
            ? throw new TemplateException($"{entry.FullName} in the package is too large ({entry.Length} bytes).")
            : entry.Open();
}
