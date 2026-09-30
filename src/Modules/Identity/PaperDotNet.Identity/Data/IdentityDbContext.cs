using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Identity.Data;

/// <summary>
/// Tenants, users, groups, roles, API tokens and preferences. Query rules as in every module (ADR-0039): locals, one
/// expression, explicit <c>TenantId</c>. Every save keeps the group closure current and, when access-relevant rows
/// changed, starts a new generation of the tenant's cached scopes (<see cref="AccessGeneration"/>).
/// </summary>
public class IdentityDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants { get; set; } = null!;

    public DbSet<User> Users { get; set; } = null!;

    public DbSet<Group> Groups { get; set; } = null!;

    public DbSet<GroupMember> GroupMembers { get; set; } = null!;

    public DbSet<GroupNesting> GroupNestings { get; set; } = null!;

    public DbSet<GroupClosure> GroupClosures { get; set; } = null!;

    public DbSet<Role> Roles { get; set; } = null!;

    public DbSet<RoleAssignment> RoleAssignments { get; set; } = null!;

    public DbSet<ApiToken> ApiTokens { get; set; } = null!;

    public DbSet<Preferences> Preferences { get; set; } = null!;

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var tenants = ChangeTracker.Entries()
            .Where(e => e.Entity is User or Group or GroupMember or GroupNesting or Role or RoleAssignment or ApiToken
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => ((PaperDotNet.Abstractions.ITenantOwned)e.Entity).TenantId)
            .ToHashSet();
        await UpdateGroupClosureAsync(cancellationToken);
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        foreach (var tenant in tenants)
        {
            AccessGeneration.Next(tenant);
        }

        return saved;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync: it keeps the group closure current.");

    /// <summary>
    /// When groups or nestings are added or removed, rebuilds the tenant's <see cref="GroupClosure"/> rows in the same
    /// save (groups are few), whichever path made the change. New rows must carry their tenant already.
    /// </summary>
    private async Task UpdateGroupClosureAsync(CancellationToken cancellationToken)
    {
        var changes = ChangeTracker.Entries()
            .Where(e => e.Entity is Group or GroupNesting && e.State is EntityState.Added or EntityState.Deleted)
            .ToList();
        if (changes.Count == 0)
        {
            return;
        }

        var context = this;
        var tenant = ((PaperDotNet.Abstractions.ITenantOwned)changes[0].Entity).TenantId;
        if (tenant == Guid.Empty || changes.Any(c => ((PaperDotNet.Abstractions.ITenantOwned)c.Entity).TenantId != tenant))
        {
            throw new InvalidOperationException("Group changes must name their tenant, one tenant per save.");
        }

        var ct = cancellationToken;
        var groups = (await context.Groups.Where(g => g.TenantId == tenant).Select(g => g.Id).ToListAsync(ct)).ToHashSet();
        var nestings = (await context.GroupNestings.AsNoTracking().Where(n => n.TenantId == tenant).Select(n => new NestingPair(n.GroupId, n.MemberGroupId)).ToListAsync(ct))
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
        foreach (var row in await context.GroupClosures.Where(c => c.TenantId == tenant).ToListAsync(ct))
        {
            if (!wanted.Remove((row.GroupId, row.AncestorId)))
            {
                GroupClosures.Remove(row);
            }
        }

        GroupClosures.AddRange(wanted.Select(w => new GroupClosure { TenantId = tenant, GroupId = w.Group, AncestorId = w.Ancestor }));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.Property(t => t.Identifier).HasMaxLength(64);
            tenant.Property(t => t.Name).HasMaxLength(200);
            tenant.Property(t => t.Status).HasMaxLength(16);
            tenant.HasIndex(t => t.Identifier).IsUnique();
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.UserName).HasMaxLength(256);
            user.Property(u => u.NormalizedUserName).HasMaxLength(256);
            user.Property(u => u.DisplayName).HasMaxLength(256);
            user.Property(u => u.Email).HasMaxLength(256);
            user.HasIndex(u => new { u.TenantId, u.NormalizedUserName }).IsUnique();
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Group>(group =>
        {
            group.ToTable("groups");
            group.Property(g => g.Name).HasMaxLength(200);
            group.Property(g => g.Description).HasMaxLength(1000);
            group.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        });
        modelBuilder.Entity<GroupMember>(member =>
        {
            member.ToTable("group_members");
            member.HasKey(m => new { m.GroupId, m.UserId });
            member.HasIndex(m => new { m.TenantId, m.UserId });
        });
        modelBuilder.Entity<GroupNesting>(nesting =>
        {
            nesting.ToTable("group_nestings");
            nesting.HasKey(n => new { n.GroupId, n.MemberGroupId });
            nesting.HasIndex(n => new { n.TenantId, n.MemberGroupId });
        });
        modelBuilder.Entity<GroupClosure>(closure =>
        {
            closure.ToTable("group_closure");
            closure.HasKey(c => new { c.GroupId, c.AncestorId });
            closure.HasIndex(c => new { c.TenantId, c.AncestorId, c.GroupId });
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.ToTable("roles");
            role.Property(r => r.Name).HasMaxLength(200);
            role.Property(r => r.Description).HasMaxLength(1000);
            role.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
        });
        modelBuilder.Entity<RoleAssignment>(assignment =>
        {
            assignment.ToTable("role_assignments");
            assignment.Property(a => a.PrincipalType).HasMaxLength(8);
            assignment.HasIndex(a => new { a.RoleId, a.PrincipalId, a.PrincipalType }).IsUnique();
            assignment.HasIndex(a => new { a.TenantId, a.PrincipalId });
        });

        modelBuilder.Entity<ApiToken>(token =>
        {
            token.ToTable("api_tokens");
            token.HasIndex(t => t.Hash).IsUnique();
            token.HasIndex(t => new { t.TenantId, t.UserId });
            token.Property(t => t.Name).HasMaxLength(200);
            token.Property(t => t.Prefix).HasMaxLength(16);
        });

        modelBuilder.Entity<Preferences>(preferences =>
        {
            preferences.ToTable("preferences");
            preferences.HasIndex(p => new { p.TenantId, p.UserId }).IsUnique();
            preferences.Property(p => p.Language).HasMaxLength(35);
            preferences.Property(p => p.TimeZone).HasMaxLength(64);
            preferences.Property(p => p.DateFormat).HasMaxLength(32);
            preferences.Property(p => p.TimeFormat).HasMaxLength(8);
            preferences.Property(p => p.NumberFormat).HasMaxLength(35);
            preferences.Property(p => p.Theme).HasMaxLength(16);
            preferences.Property(p => p.DocumentLanguages).HasMaxLength(100);
        });
    }
}

/// <summary>
/// A counter per tenant that changes whenever users, groups, roles or tokens change: caches of what a user may do
/// (effective scopes) include it in their keys, so a change applies to the next request on this server. Other
/// servers catch up when their cache entries expire.
/// </summary>
public static class AccessGeneration
{
    private static readonly ConcurrentDictionary<Guid, long> Generations = new();

    public static long Current(Guid tenantId) => Generations.GetValueOrDefault(tenantId);

    public static void Next(Guid tenantId) => Generations.AddOrUpdate(tenantId, 1, (_, value) => value + 1);
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<IdentityDbContext>());
}
