using PaperDotNet.Storage;

namespace PaperDotNet.IntegrationTests;

public sealed class StorageTests
{
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
