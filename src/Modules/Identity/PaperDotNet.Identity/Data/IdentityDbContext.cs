using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Identity.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenant)
    : IdentityUserContext<User, Guid>(options), ITenantScopedDbContext
{
    public const string Schema = "identity";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.Property(u => u.DisplayName).HasMaxLength(200);

            // User names are unique per tenant, not globally.
            b.HasIndex(u => u.NormalizedUserName).HasDatabaseName("UserNameIndex").IsUnique(false);
            b.HasIndex(u => new { u.TenantId, u.NormalizedUserName }).IsUnique();
        });
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");

        builder.Entity<Group>(b =>
        {
            b.Property(g => g.Name).HasMaxLength(200);
            b.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        });
        builder.Entity<GroupMember>(b => b.HasKey(m => new { m.GroupId, m.UserId }));

        builder.Entity<Role>(b =>
        {
            b.Property(r => r.Name).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
        });
        builder.Entity<RoleAssignment>(b =>
            b.HasIndex(a => new { a.RoleId, a.PrincipalId, a.PrincipalType }).IsUnique());

        builder.Entity<ApiToken>(b =>
        {
            b.HasIndex(t => t.Hash).IsUnique();
            b.HasIndex(t => new { t.TenantId, t.UserId });
            b.Property(t => t.Name).HasMaxLength(200);
            b.Property(t => t.Prefix).HasMaxLength(16);
            b.Property(t => t.TenantIdentifier).HasMaxLength(63);
        });

        builder.ApplyPaperDotNetConventions(this);
    }
}
