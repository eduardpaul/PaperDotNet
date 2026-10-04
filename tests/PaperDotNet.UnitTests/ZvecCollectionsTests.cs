using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaperDotNet.Search.Zvec;
using PaperDotNet.Search.Zvec.Native;

namespace PaperDotNet.UnitTests;

public sealed class ZvecCollectionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_freeze_releases_waiters_and_retry_waits_for_active_leases(bool cancelBeforeFreeze)
    {
        using var collections = new ZvecCollections(Options.Create(new ZvecOptions()), NullLogger<ZvecCollections>.Instance);
        var index = AddLeasedIndex(collections);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        if (cancelBeforeFreeze)
        {
            await stop.CancelAsync();
        }

        var freezing = collections.FreezeAsync(stop.Token);
        var released = Frozen(collections)?.Task;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => freezing);
        if (released is not null)
        {
            await released.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }

        Assert.Null(Frozen(collections));
        Assert.False(index.Closed.IsCompleted);

        // Cancellation unblocks callers, but the native folder must not be reopened until its active lease ends.
        var retry = collections.FreezeAsync(Ct);
        Assert.False(retry.IsCompleted);
        index.Release();
        await using var frozen = await retry.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(index.Closed.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Disposing_an_old_freeze_twice_does_not_release_a_new_freeze()
    {
        using var collections = new ZvecCollections(Options.Create(new ZvecOptions()), NullLogger<ZvecCollections>.Instance);
        var first = await collections.FreezeAsync(Ct);
        await first.DisposeAsync();

        await using var second = await collections.FreezeAsync(Ct);
        var released = Frozen(collections)!.Task;
        await first.DisposeAsync();

        Assert.False(released.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => collections.FreezeAsync(Ct));
        Assert.False(released.IsCompleted);
        await second.DisposeAsync();
        await released.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await using var third = await collections.FreezeAsync(Ct);
    }

    private static ZvecIndex AddLeasedIndex(ZvecCollections collections)
    {
        // A zero native handle makes disposal a no-op, so lifecycle tests run without the optional zvec library.
        var native = (ZvecCollection)RuntimeHelpers.GetUninitializedObject(typeof(ZvecCollection));
        var path = Path.Combine(Path.GetTempPath(), $"pdn_zvec_lifecycle_{Guid.NewGuid():N}");
        var index = new ZvecIndex(native, ZvecCatalog.Load(path), vectors: null);
        Assert.True(index.TryAcquire());
        Open(collections).Add(path, index);
        return index;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_open")]
    private static extern ref Dictionary<string, ZvecIndex> Open(ZvecCollections collections);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_frozen")]
    private static extern ref TaskCompletionSource? Frozen(ZvecCollections collections);
}
