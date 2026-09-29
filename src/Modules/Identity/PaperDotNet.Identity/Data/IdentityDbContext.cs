using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Identity.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenant)
    : IdentityUserContext<User, Guid>(options), ITenantScopedDbContext, IDataProtectionKeyContext
{
    public const string Schema = "identity";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<GroupNesting> GroupNestings => Set<GroupNesting>();

    public DbSet<GroupClosure> GroupClosures => Set<GroupClosure>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    public DbSet<Preferences> Preferences => Set<Preferences>();

    /// <summary>ASP.NET Data Protection key ring, shared by all nodes (not tenant-owned).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<ServerKey> ServerKeys => Set<ServerKey>();

    public DbSet<OAuthApplication> Applications => Set<OAuthApplication>();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await UpdateGroupClosureAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateGroupClosureAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// When groups or nestings are added or removed, rebuilds the tenant's <see cref="GroupClosure"/> rows in the
    /// same save (groups are few), whichever path made the change.
    /// </summary>
    private async Task UpdateGroupClosureAsync(CancellationToken ct)
    {
        var changes = ChangeTracker.Entries()
            .Where(e => e.Entity is Group or GroupNesting && e.State is EntityState.Added or EntityState.Deleted)
            .ToList();
        if (changes.Count == 0)
        {
            return;
        }

        var groups = (await Groups.AsNoTracking().Select(g => g.Id).ToListAsync(ct)).ToHashSet();
        var nestings = (await GroupNestings.AsNoTracking().Select(n => new { n.GroupId, n.MemberGroupId }).ToListAsync(ct))
            .Select(n => (n.GroupId, n.MemberGroupId))
            .ToHashSet();
        foreach (var change in changes)
        {
            var added = change.State == EntityState.Added;
            switch (change.Entity)
            {
                case Group group when added:
                    groups.Add(group.Id);
                    break;
                case Group group:
                    groups.Remove(group.Id);
                    break;
                case GroupNesting nesting when added:
                    nestings.Add((nesting.GroupId, nesting.MemberGroupId));
                    break;
                case GroupNesting nesting:
                    nestings.Remove((nesting.GroupId, nesting.MemberGroupId));
                    break;
            }
        }

        nestings.RemoveWhere(n => !groups.Contains(n.GroupId) || !groups.Contains(n.MemberGroupId));
        var wanted = GroupGraph.Closure(groups, nestings);
        foreach (var row in await GroupClosures.ToListAsync(ct))
        {
            if (!wanted.Remove((row.GroupId, row.AncestorId)))
            {
                GroupClosures.Remove(row);
            }
        }

        GroupClosures.AddRange(wanted.Select(w => new GroupClosure { GroupId = w.Group, AncestorId = w.Ancestor }));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Schema version 3 (passkeys) comes from IdentityOptions.Stores.SchemaVersion (design-time factories set it too).
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
        if (builder.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>)) is not null)
        {
            // Only when Identity mapped passkeys (schema version 3); otherwise this would add an incomplete entity.
            builder.Entity<IdentityUserPasskey<Guid>>().ToTable("user_passkeys");
        }
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
        builder.Entity<ServerKey>(b =>
        {
            b.ToTable("server_keys");
            b.Property(k => k.Use).HasMaxLength(8);
        });

        builder.UseOpenIddict<OAuthApplication, OAuthAuthorization, OAuthScope, OAuthToken, Guid>();
        builder.Entity<OAuthApplication>(b =>
        {
            b.ToTable("oauth_applications");

            // Client ids are unique per tenant (the first-party client exists in every tenant).
            b.HasIndex(a => a.ClientId).IsUnique(false);
            b.HasIndex(a => new { a.TenantId, a.ClientId }).IsUnique();
        });
        builder.Entity<OAuthAuthorization>().ToTable("oauth_authorizations");
        builder.Entity<OAuthScope>().ToTable("oauth_scopes");
        builder.Entity<OAuthToken>().ToTable("oauth_tokens");

        builder.Entity<Group>(b =>
        {
            b.Property(g => g.Name).HasMaxLength(200);
            b.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        });
        builder.Entity<GroupMember>(b => b.HasKey(m => new { m.GroupId, m.UserId }));
        builder.Entity<GroupNesting>(b =>
        {
            b.HasKey(n => new { n.GroupId, n.MemberGroupId });
            b.HasIndex(n => n.MemberGroupId);
        });
        builder.Entity<GroupClosure>(b =>
        {
            b.ToTable("group_closure");
            b.HasKey(c => new { c.GroupId, c.AncestorId });
            b.HasIndex(c => new { c.AncestorId, c.GroupId });
        });

        builder.Entity<Role>(b =>
        {
            b.Property(r => r.Name).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
        });
        builder.Entity<RoleAssignment>(b =>
            b.HasIndex(a => new { a.RoleId, a.PrincipalId, a.PrincipalType }).IsUnique());

        builder.Entity<Preferences>(b =>
        {
            b.ToTable("preferences");
            b.HasIndex(p => new { p.TenantId, p.UserId }).IsUnique();
            b.Property(p => p.Language).HasMaxLength(35);
            b.Property(p => p.TimeZone).HasMaxLength(64);
            b.Property(p => p.DateFormat).HasMaxLength(32);
            b.Property(p => p.TimeFormat).HasMaxLength(8);
            b.Property(p => p.NumberFormat).HasMaxLength(35);
            b.Property(p => p.Theme).HasMaxLength(16);
            b.Property(p => p.DocumentLanguages).HasMaxLength(100);
        });

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
