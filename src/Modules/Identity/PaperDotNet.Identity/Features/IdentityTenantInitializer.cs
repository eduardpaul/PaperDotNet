using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>Creates the built-in Administrator and Member roles and the first-party OAuth client for a new tenant.</summary>
internal sealed class IdentityTenantInitializer(
    IdentityDbContext db, IScopeCatalog catalog, OpenIddict.Abstractions.IOpenIddictApplicationManager applications,
    Microsoft.Extensions.Options.IOptions<Authentication.AuthOptions> options) : ITenantInitializer
{
    public async Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        db.Roles.Add(new Role
        {
            Id = Ids.New(),
            Name = Role.Administrator,
            Description = "Full access to the organization.",
            IsBuiltIn = true,
            GrantsAllScopes = true,
        });
        db.Roles.Add(new Role
        {
            Id = Ids.New(),
            Name = Role.Member,
            Description = "Everyday use: workspaces, documents, tasks and events.",
            IsBuiltIn = true,
            Scopes = catalog.All.Where(s => s.GrantedToMembers).Select(s => s.Name).ToList(),
        });
        await db.SaveChangesAsync(cancellationToken);
        await FirstPartyClient.EnsureAsync(applications, options.Value, cancellationToken);
    }
}
