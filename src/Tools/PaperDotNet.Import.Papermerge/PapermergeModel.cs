using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Import.Papermerge;

// Read-only model of the Papermerge 3.6 tables the import needs (papermerge-core, Apache-2.0). Enum columns are read as text.
#pragma warning disable CA1812 // Instantiated by EF Core.

internal sealed class PmNode
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Ctype { get; set; } = string.Empty;
    public string? Lang { get; set; }
    public Guid? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class PmDocument
{
    public Guid NodeId { get; set; }
    public Guid? DocumentTypeId { get; set; }
}

internal sealed class PmDocumentVersion
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
    public Guid DocumentId { get; set; }
    public string? Lang { get; set; }
    public string? CreationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class PmPage
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string? Text { get; set; }
    public Guid DocumentVersionId { get; set; }
}

internal sealed class PmTag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? BgColor { get; set; }
    public string? Description { get; set; }
}

internal sealed class PmNodeTag
{
    public int Id { get; set; }
    public Guid NodeId { get; set; }
    public Guid TagId { get; set; }
}

internal sealed class PmCustomField
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeHandler { get; set; } = string.Empty;
    public string? Config { get; set; }
}

internal sealed class PmCustomFieldValue
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid FieldId { get; set; }
    public string? Value { get; set; }
    public string? ValueText { get; set; }
    public decimal? ValueNumeric { get; set; }
    public DateOnly? ValueDate { get; set; }
    public DateTimeOffset? ValueDatetime { get; set; }
    public bool? ValueBoolean { get; set; }
}

internal sealed class PmDocumentType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PathTemplate { get; set; }
}

internal sealed class PmDocumentTypeField
{
    public int Id { get; set; }
    public Guid DocumentTypeId { get; set; }
    public Guid CustomFieldId { get; set; }
    public int Position { get; set; }
}

internal sealed class PmUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool IsActive { get; set; }
    public bool IsSuperuser { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class PmGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class PmUserGroup
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

internal sealed class PmOwnership
{
    public int Id { get; set; }
    public string OwnerType { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
}

internal sealed class PmSpecialFolder
{
    public Guid Id { get; set; }
    public string OwnerType { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string FolderType { get; set; } = string.Empty;
    public Guid FolderId { get; set; }
}

internal sealed class PmSharedNode
{
    public Guid Id { get; set; }
    public Guid NodeId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid RoleId { get; set; }
}

internal sealed class PmRolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

internal sealed class PmPermission
{
    public Guid Id { get; set; }
    public string Codename { get; set; } = string.Empty;
}

internal sealed class PmAlembicVersion
{
    public string VersionNum { get; set; } = string.Empty;
}

#pragma warning restore CA1812

/// <summary>Papermerge's database, read-only (no tracking, no migrations).</summary>
internal sealed class PapermergeDbContext(DbContextOptions<PapermergeDbContext> options) : DbContext(options)
{
    public DbSet<PmNode> Nodes => Set<PmNode>();
    public DbSet<PmDocument> Documents => Set<PmDocument>();
    public DbSet<PmDocumentVersion> Versions => Set<PmDocumentVersion>();
    public DbSet<PmPage> Pages => Set<PmPage>();
    public DbSet<PmTag> Tags => Set<PmTag>();
    public DbSet<PmNodeTag> NodeTags => Set<PmNodeTag>();
    public DbSet<PmCustomField> CustomFields => Set<PmCustomField>();
    public DbSet<PmCustomFieldValue> CustomFieldValues => Set<PmCustomFieldValue>();
    public DbSet<PmDocumentType> DocumentTypes => Set<PmDocumentType>();
    public DbSet<PmDocumentTypeField> DocumentTypeFields => Set<PmDocumentTypeField>();
    public DbSet<PmUser> Users => Set<PmUser>();
    public DbSet<PmGroup> Groups => Set<PmGroup>();
    public DbSet<PmUserGroup> UserGroups => Set<PmUserGroup>();
    public DbSet<PmOwnership> Ownerships => Set<PmOwnership>();
    public DbSet<PmSpecialFolder> SpecialFolders => Set<PmSpecialFolder>();
    public DbSet<PmSharedNode> SharedNodes => Set<PmSharedNode>();
    public DbSet<PmRolePermission> RolePermissions => Set<PmRolePermission>();
    public DbSet<PmPermission> Permissions => Set<PmPermission>();
    public DbSet<PmAlembicVersion> AlembicVersion => Set<PmAlembicVersion>();

    /// <summary>Opens the database; its enum columns (e.g. <c>owner_type_enum</c>) are read as text.</summary>
    public static (PapermergeDbContext Db, Npgsql.NpgsqlDataSource DataSource) Open(string connectionString)
    {
        var builder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        builder.EnableUnmappedTypes();
        var dataSource = builder.Build();
        var db = new PapermergeDbContext(new DbContextOptionsBuilder<PapermergeDbContext>()
            .UseNpgsql(dataSource)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
        return (db, dataSource);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PmNode>(b =>
        {
            b.ToTable("nodes");
            b.Property(n => n.Id).HasColumnName("id");
            b.Property(n => n.Title).HasColumnName("title");
            b.Property(n => n.Ctype).HasColumnName("ctype");
            b.Property(n => n.Lang).HasColumnName("lang");
            b.Property(n => n.ParentId).HasColumnName("parent_id");
            Audit(b);
        });
        modelBuilder.Entity<PmDocument>(b =>
        {
            b.ToTable("documents");
            b.HasKey(d => d.NodeId);
            b.Property(d => d.NodeId).HasColumnName("node_id");
            b.Property(d => d.DocumentTypeId).HasColumnName("document_type_id");
        });
        modelBuilder.Entity<PmDocumentVersion>(b =>
        {
            b.ToTable("document_versions");
            b.Property(v => v.Id).HasColumnName("id");
            b.Property(v => v.Number).HasColumnName("number");
            b.Property(v => v.FileName).HasColumnName("file_name");
            b.Property(v => v.MimeType).HasColumnName("mime_type");
            b.Property(v => v.DocumentId).HasColumnName("document_id");
            b.Property(v => v.Lang).HasColumnName("lang");
            b.Property(v => v.CreationReason).HasColumnName("creation_reason");
            b.Property(v => v.CreatedAt).HasColumnName("created_at");
            b.Property(v => v.CreatedBy).HasColumnName("created_by");
            b.Property(v => v.DeletedAt).HasColumnName("deleted_at");
        });
        modelBuilder.Entity<PmPage>(b =>
        {
            b.ToTable("pages");
            b.Property(p => p.Id).HasColumnName("id");
            b.Property(p => p.Number).HasColumnName("number");
            b.Property(p => p.Text).HasColumnName("text");
            b.Property(p => p.DocumentVersionId).HasColumnName("document_version_id");
        });
        modelBuilder.Entity<PmTag>(b =>
        {
            b.ToTable("tags");
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.Name).HasColumnName("name");
            b.Property(t => t.BgColor).HasColumnName("bg_color");
            b.Property(t => t.Description).HasColumnName("description");
        });
        modelBuilder.Entity<PmNodeTag>(b =>
        {
            b.ToTable("nodes_tags");
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.NodeId).HasColumnName("node_id");
            b.Property(t => t.TagId).HasColumnName("tag_id");
        });
        modelBuilder.Entity<PmCustomField>(b =>
        {
            b.ToTable("custom_fields");
            b.Property(f => f.Id).HasColumnName("id");
            b.Property(f => f.Name).HasColumnName("name");
            b.Property(f => f.TypeHandler).HasColumnName("type_handler");
            b.Property(f => f.Config).HasColumnName("config").HasColumnType("jsonb");
        });
        modelBuilder.Entity<PmCustomFieldValue>(b =>
        {
            b.ToTable("custom_field_values");
            b.Property(v => v.Id).HasColumnName("id");
            b.Property(v => v.DocumentId).HasColumnName("document_id");
            b.Property(v => v.FieldId).HasColumnName("field_id");
            b.Property(v => v.Value).HasColumnName("value").HasColumnType("jsonb");
            b.Property(v => v.ValueText).HasColumnName("value_text");
            b.Property(v => v.ValueNumeric).HasColumnName("value_numeric");
            b.Property(v => v.ValueDate).HasColumnName("value_date");
            b.Property(v => v.ValueDatetime).HasColumnName("value_datetime");
            b.Property(v => v.ValueBoolean).HasColumnName("value_boolean");
        });
        modelBuilder.Entity<PmDocumentType>(b =>
        {
            b.ToTable("document_types");
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.Name).HasColumnName("name");
            b.Property(t => t.PathTemplate).HasColumnName("path_template");
        });
        modelBuilder.Entity<PmDocumentTypeField>(b =>
        {
            b.ToTable("document_types_custom_fields");
            b.Property(f => f.Id).HasColumnName("id");
            b.Property(f => f.DocumentTypeId).HasColumnName("document_type_id");
            b.Property(f => f.CustomFieldId).HasColumnName("custom_field_id");
            b.Property(f => f.Position).HasColumnName("position");
        });
        modelBuilder.Entity<PmUser>(b =>
        {
            b.ToTable("users");
            b.Property(u => u.Id).HasColumnName("id");
            b.Property(u => u.Username).HasColumnName("username");
            b.Property(u => u.Email).HasColumnName("email");
            b.Property(u => u.FirstName).HasColumnName("first_name");
            b.Property(u => u.LastName).HasColumnName("last_name");
            b.Property(u => u.IsActive).HasColumnName("is_active");
            b.Property(u => u.IsSuperuser).HasColumnName("is_superuser");
            b.Property(u => u.DeletedAt).HasColumnName("deleted_at");
        });
        modelBuilder.Entity<PmGroup>(b =>
        {
            b.ToTable("groups");
            b.Property(g => g.Id).HasColumnName("id");
            b.Property(g => g.Name).HasColumnName("name");
        });
        modelBuilder.Entity<PmUserGroup>(b =>
        {
            b.ToTable("users_groups");
            b.Property(g => g.Id).HasColumnName("id");
            b.Property(g => g.GroupId).HasColumnName("group_id");
            b.Property(g => g.UserId).HasColumnName("user_id");
        });
        modelBuilder.Entity<PmOwnership>(b =>
        {
            b.ToTable("ownerships");
            b.Property(o => o.Id).HasColumnName("id");
            b.Property(o => o.OwnerType).HasColumnName("owner_type");
            b.Property(o => o.OwnerId).HasColumnName("owner_id");
            b.Property(o => o.ResourceType).HasColumnName("resource_type");
            b.Property(o => o.ResourceId).HasColumnName("resource_id");
        });
        modelBuilder.Entity<PmSpecialFolder>(b =>
        {
            b.ToTable("special_folders");
            b.Property(f => f.Id).HasColumnName("id");
            b.Property(f => f.OwnerType).HasColumnName("owner_type");
            b.Property(f => f.OwnerId).HasColumnName("owner_id");
            b.Property(f => f.FolderType).HasColumnName("folder_type");
            b.Property(f => f.FolderId).HasColumnName("folder_id");
        });
        modelBuilder.Entity<PmSharedNode>(b =>
        {
            b.ToTable("shared_nodes");
            b.Property(s => s.Id).HasColumnName("id");
            b.Property(s => s.NodeId).HasColumnName("node_id");
            b.Property(s => s.UserId).HasColumnName("user_id");
            b.Property(s => s.GroupId).HasColumnName("group_id");
            b.Property(s => s.RoleId).HasColumnName("role_id");
        });
        modelBuilder.Entity<PmRolePermission>(b =>
        {
            b.ToTable("roles_permissions");
            b.HasNoKey();
            b.Property(r => r.RoleId).HasColumnName("role_id");
            b.Property(r => r.PermissionId).HasColumnName("permission_id");
        });
        modelBuilder.Entity<PmPermission>(b =>
        {
            b.ToTable("permissions");
            b.Property(p => p.Id).HasColumnName("id");
            b.Property(p => p.Codename).HasColumnName("codename");
        });
        modelBuilder.Entity<PmAlembicVersion>(b =>
        {
            b.ToTable("alembic_version");
            b.HasNoKey();
            b.Property(a => a.VersionNum).HasColumnName("version_num");
        });
    }

    private static void Audit(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PmNode> b)
    {
        b.Property(n => n.CreatedAt).HasColumnName("created_at");
        b.Property(n => n.UpdatedAt).HasColumnName("updated_at");
        b.Property(n => n.CreatedBy).HasColumnName("created_by");
        b.Property(n => n.UpdatedBy).HasColumnName("updated_by");
        b.Property(n => n.DeletedAt).HasColumnName("deleted_at");
    }
}
