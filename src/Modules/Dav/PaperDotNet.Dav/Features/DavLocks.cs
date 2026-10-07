using System.Xml.Linq;
using FubarDev.WebDavServer;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Dav.Data;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.Dav.Features;

/// <summary>
/// WebDAV locks in the database (ADR-0047): the library's lock rules (<see cref="LockManagerBase"/>), our storage, so
/// locks hold across requests, restarts and servers. Paths compare case-insensitively, like the tree. <c>Home</c> is a
/// different workspace for every user, so locks under it only apply to their holder. The library takes an infinite lock
/// for the duration of each write; stored, such locks expire after <see cref="DavOptions.MaxLockTimeout"/>, so a request
/// that never finished cannot block a file for good.
/// </summary>
internal sealed class DavLockManager(
    DavDbContext db, ICurrentUser user, TimeProvider time, IOptions<DavOptions> options, IWebDavContextAccessor context, ILockCleanupTask cleanup,
    ISystemClock clock, ILogger<DavLockManager> logger) : LockManagerBase(context, cleanup, clock, logger)
{
    private const string Home = "Home/";

    protected override Uri NormalizePath(Uri path) => new(path.AbsoluteUri.ToUpperInvariant());

    protected override Task<ILockManagerTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ILockManagerTransaction>(new Transaction(db, user.UserId ?? Guid.Empty, time.GetUtcNow(), options.Value.MaxLockTimeout));

    private sealed class Transaction(DavDbContext db, Guid userId, DateTimeOffset now, TimeSpan maxTimeout) : ILockManagerTransaction
    {
        public async Task<IReadOnlyCollection<IActiveLock>> GetActiveLocksAsync(CancellationToken cancellationToken)
        {
            var locks = await db.Locks.AsNoTracking().Where(l => l.Expiration > now).ToListAsync(cancellationToken);
            return locks.Where(Applies).Select(ToActiveLock).ToList();
        }

        public Task<bool> AddAsync(IActiveLock activeLock, CancellationToken cancellationToken)
        {
            db.Locks.Add(new DavLock
            {
                Id = Ids.New(),
                StateToken = activeLock.StateToken,
                Path = activeLock.Path,
                Href = activeLock.Href,
                Recursive = activeLock.Recursive,
                AccessType = activeLock.AccessType,
                ShareMode = activeLock.ShareMode,
                TimeoutSeconds = Seconds(activeLock.Timeout),
                Owner = activeLock.Owner,
                OwnerHref = activeLock.GetOwnerHref()?.ToString(SaveOptions.DisableFormatting),
                UserId = userId,
                Issued = Utc(activeLock.Issued),
                LastRefresh = activeLock.LastRefresh is { } refreshed ? Utc(refreshed) : null,
                Expiration = Expiration(activeLock),
            });
            return Task.FromResult(true);
        }

        public async Task<bool> UpdateAsync(IActiveLock activeLock, CancellationToken cancellationToken)
        {
            var row = await db.Locks.FirstOrDefaultAsync(l => l.StateToken == activeLock.StateToken, cancellationToken);
            if (row is null)
            {
                await AddAsync(activeLock, cancellationToken);
                return false;
            }

            row.TimeoutSeconds = Seconds(activeLock.Timeout);
            row.LastRefresh = activeLock.LastRefresh is { } refreshed ? Utc(refreshed) : null;
            row.Expiration = Expiration(activeLock);
            return true;
        }

        public async Task<bool> RemoveAsync(string stateToken, CancellationToken cancellationToken)
        {
            var row = await db.Locks.FirstOrDefaultAsync(l => l.StateToken == stateToken, cancellationToken);
            if (row is null)
            {
                return false;
            }

            db.Locks.Remove(row);
            return true;
        }

        public async Task<IActiveLock?> GetAsync(string stateToken, CancellationToken cancellationToken)
        {
            var row = await db.Locks.AsNoTracking().FirstOrDefaultAsync(l => l.StateToken == stateToken && l.Expiration > now, cancellationToken);
            return row is not null && Applies(row) ? ToActiveLock(row) : null;
        }

        public Task CommitAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

        public void Dispose()
        {
        }

        private bool Applies(DavLock row) => row.UserId == userId || !row.Path.StartsWith(Home, StringComparison.OrdinalIgnoreCase);

        private DateTimeOffset Expiration(IActiveLock activeLock) =>
            activeLock.Expiration == DateTime.MaxValue ? now + maxTimeout : Utc(activeLock.Expiration);

        private static long Seconds(TimeSpan timeout) => timeout == TimeoutHeader.Infinite ? -1 : (long)timeout.TotalSeconds;

        private static DateTimeOffset Utc(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private static DavActiveLock ToActiveLock(DavLock row) => new(
            row.Path, row.Href, row.Recursive, row.Owner, row.OwnerHref is null ? null : XElement.Parse(row.OwnerHref), row.AccessType,
            row.ShareMode, row.TimeoutSeconds < 0 ? TimeoutHeader.Infinite : TimeSpan.FromSeconds(row.TimeoutSeconds), row.StateToken,
            row.Issued.UtcDateTime, row.LastRefresh?.UtcDateTime, row.Expiration.UtcDateTime);
    }
}

/// <summary>A stored lock as the library sees it.</summary>
internal sealed record DavActiveLock(
    string Path, string Href, bool Recursive, string? Owner, XElement? OwnerHref, string AccessType, string ShareMode, TimeSpan Timeout,
    string StateToken, DateTime Issued, DateTime? LastRefresh, DateTime Expiration) : IActiveLock
{
    public XElement? GetOwnerHref() => OwnerHref;
}

/// <summary>Expired locks are ignored when read and removed by <c>dav.cleanup</c>, not by a timer per server.</summary>
internal sealed class DavLockCleanup : ILockCleanupTask
{
    public void Add(ILockManager lockManager, IActiveLock activeLock)
    {
    }

    public void Remove(IActiveLock activeLock)
    {
    }
}
