using System.Text;
using System.Text.Json.Nodes;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Provisioning.Features;

namespace PaperDotNet.UnitTests;

/// <summary>Template packages (PRV-04).</summary>
public sealed class TemplatePackageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Packages_store_files_once_and_read_back_what_was_written()
    {
        using var buffer = new MemoryStream();
        string first, second;
        await using (var package = ZipTemplatePackage.Create(new NonDisposingStream(buffer)))
        {
            first = await package.AddFileAsync(new MemoryStream("same"u8.ToArray()), Ct);
            second = await package.AddFileAsync(new MemoryStream("same"u8.ToArray()), Ct);
            await package.WriteJsonAsync("content/a.json", new JsonObject { ["x"] = 1 }, Ct);
            await package.WriteTemplateAsync(new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(TemplateXml.Name("Template"))), Ct);
        }

        Assert.Equal(first, second);
        Assert.StartsWith("files/", first, StringComparison.Ordinal);
        buffer.Position = 0;
        await using var read = ZipTemplatePackage.Open(buffer, leaveOpen: true);
        Assert.Equal(1, (await read.ReadJsonAsync("content/a.json", Ct))!["x"]!.GetValue<int>());
        Assert.Null(await read.ReadJsonAsync("content/missing.json", Ct));
        using var reader = new StreamReader(read.OpenFile(first)!, Encoding.UTF8);
        Assert.Equal("same", await reader.ReadToEndAsync(Ct));
        Assert.Equal("Template", read.ReadTemplate().Root!.Name.LocalName);
    }

    [Fact]
    public void Invalid_packages_and_content_ids_are_handled()
    {
        Assert.Throws<TemplateException>(() => ZipTemplatePackage.Open(new MemoryStream("no zip"u8.ToArray())));
        var list = Guid.NewGuid();
        Assert.Equal(TemplateContent.ItemId(list, "a"), TemplateContent.ItemId(list, "a"));
        Assert.NotEqual(TemplateContent.ItemId(list, "a"), TemplateContent.ItemId(Guid.NewGuid(), "a"));
        Assert.NotEqual(TemplateContent.ItemId(list, "a"), TemplateContent.ItemId(list, "b"));
    }

    /// <summary>Keeps the buffer readable after the package is disposed.</summary>
    private sealed class NonDisposingStream(Stream inner) : MemoryStream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin loc) => inner.Seek(offset, loc);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Flush() => inner.Flush();
    }
}
