using PaperDotNet.Documents.Features;
using PaperDotNet.Storage;

namespace PaperDotNet.UnitTests;

public sealed class DocumentStorageTests
{
    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, FileTypes.Pdf)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08 }, FileTypes.Tiff)]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00 }, FileTypes.Tiff)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FileTypes.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, FileTypes.Png)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null)] // zip / docx
    [InlineData(new byte[] { 0x25, 0x50 }, null)]
    [InlineData(new byte[0], null)]
    public void File_types_are_detected_by_their_first_bytes(byte[] header, string? expected) =>
        Assert.Equal(expected, FileTypes.Detect(header));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("UPPER")]
    [InlineData("a/../b")]
    [InlineData("")]
    public void Blob_keys_cannot_escape_the_root(string key)
    {
        var store = new LocalBlobStore(Path.Combine(Path.GetTempPath(), $"pdn_blobs_{Guid.NewGuid():N}"));
        Assert.Throws<ArgumentException>(() => store.PathFor(key));
    }

    [Fact]
    public async Task Blobs_are_written_read_and_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pdn_blobs_{Guid.NewGuid():N}");
        var store = new LocalBlobStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await store.WriteAsync("t1/ab/abcdef", new MemoryStream([1, 2, 3]), ct);
            await using (var read = await store.OpenReadAsync("t1/ab/abcdef", ct))
            {
                var copy = new MemoryStream();
                await read!.CopyToAsync(copy, ct);
                Assert.Equal([1, 2, 3], copy.ToArray());
            }

            await store.DeleteAsync("t1/ab/abcdef", ct);
            Assert.False(await store.ExistsAsync("t1/ab/abcdef", ct));
            Assert.Null(await store.OpenReadAsync("t1/ab/abcdef", ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
