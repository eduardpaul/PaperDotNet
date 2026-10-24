using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Content;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.Host.Content;

internal sealed class ContentSession(ContentDatabaseFactory factory, ITenantContext tenant, ICurrentUser user,
    IUserDirectory directory, IHttpContextAccessor http, IAuthorizationService authorization, TimeProvider time) : IAsyncDisposable
{
    private ContentDbContext? database;
    public string Actor => user.UserId?.ToString() ?? throw new UnauthorizedAccessException("Authentication is required.");
    public ContentDbContext Db => database ?? throw new InvalidOperationException("Content session is not open.");
    public ContentAccess Access { get; private set; } = null!;
    public ContentStore Store { get; private set; } = null!;

    public async Task OpenAsync(CancellationToken ct)
    {
        if (database is not null) return;
        if (tenant.TenantId is not { } tenantId || user.UserId is not { } userId || !await directory.IsActiveAsync(userId, ct))
            throw new UnauthorizedAccessException("An active organization user is required.");
        database = await factory.OpenAsync(tenantId, ct);
        Access = new ContentAccess(database, directory, user, http, authorization);
        Store = new ContentStore(database, Access, time);
        if (!await database.Set<IdentityOrganizationsRow>().AnyAsync(ct))
            database.Set<IdentityOrganizationsRow>().Add(new IdentityOrganizationsRow { Id = tenantId.ToString(), Identifier = tenant.TenantIdentifier ?? tenantId.ToString("N"), DisplayName = tenant.TenantIdentifier ?? "Organization" });
        if (!await database.Set<IdentityUsersRow>().AnyAsync(x => x.Id == Actor, ct))
            database.Set<IdentityUsersRow>().Add(new IdentityUsersRow { Id = Actor, DisplayName = http.HttpContext?.User.Identity?.Name ?? Actor });
        try { await database.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            // Concurrent first requests may insert the same organization/user. Only accept the fully initialized result.
            if (!await database.Set<IdentityOrganizationsRow>().AnyAsync(ct) || !await database.Set<IdentityUsersRow>().AnyAsync(x => x.Id == Actor, ct)) throw;
        }
    }

    public async ValueTask DisposeAsync() { if (database is not null) await database.DisposeAsync(); }
}
