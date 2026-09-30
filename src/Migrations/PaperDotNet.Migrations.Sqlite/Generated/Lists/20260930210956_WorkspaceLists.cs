using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists;

/// <inheritdoc />
public partial class _20260930210956_WorkspaceLists : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_lists_TenantId_Name",
            table: "lists");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Title",
            table: "list_items");

        // Lists of pre-release builds belonged to the tenant, not to a workspace: they cannot be placed, so they go.
        migrationBuilder.Sql("DELETE FROM \"list_items\"; DELETE FROM \"lists\";");

        migrationBuilder.DropColumn(
            name: "Fields",
            table: "lists");

        migrationBuilder.AddColumn<Guid>(
            name: "WorkspaceId",
            table: "lists",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<bool>(
            name: "AllowFolders",
            table: "lists",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "ContentTypeIds",
            table: "lists",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "lists",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "DeletedBy",
            table: "lists",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "HasUniquePermissions",
            table: "lists",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "Kind",
            table: "lists",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "MaxVersions",
            table: "lists",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "SystemKey",
            table: "lists",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TemplateKey",
            table: "lists",
            type: "TEXT",
            maxLength: 150,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Versioning",
            table: "lists",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<Guid>(
            name: "ContentTypeId",
            table: "list_items",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "list_items",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "DeletedBy",
            table: "list_items",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "HasUniquePermissions",
            table: "list_items",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsFolder",
            table: "list_items",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<Guid>(
            name: "ParentId",
            table: "list_items",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ScopeId",
            table: "list_items",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.CreateTable(
            name: "acl_entries",
            columns: table => new
            {
                ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrincipalType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Level = table.Column<int>(type: "INTEGER", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_acl_entries", x => new { x.ScopeId, x.PrincipalId });
            });

        migrationBuilder.CreateTable(
            name: "content_types",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                Key = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                ExtensionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Fields = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_content_types", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_lists_TenantId_WorkspaceId",
            table: "lists",
            columns: new[] { "TenantId", "WorkspaceId" });

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_ParentId_IsFolder_Title",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "ParentId", "IsFolder", "Title" });

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ScopeId",
            table: "list_items",
            columns: new[] { "TenantId", "ScopeId" });

        migrationBuilder.CreateIndex(
            name: "IX_acl_entries_TenantId_ListId",
            table: "acl_entries",
            columns: new[] { "TenantId", "ListId" });

        migrationBuilder.CreateIndex(
            name: "IX_acl_entries_TenantId_PrincipalId_ListId",
            table: "acl_entries",
            columns: new[] { "TenantId", "PrincipalId", "ListId" });

        migrationBuilder.CreateIndex(
            name: "IX_content_types_TenantId_Key",
            table: "content_types",
            columns: new[] { "TenantId", "Key" });

        migrationBuilder.CreateIndex(
            name: "IX_content_types_TenantId_Name",
            table: "content_types",
            columns: new[] { "TenantId", "Name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "acl_entries");

        migrationBuilder.DropTable(
            name: "content_types");

        migrationBuilder.DropIndex(
            name: "IX_lists_TenantId_WorkspaceId",
            table: "lists");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_ParentId_IsFolder_Title",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ScopeId",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "AllowFolders",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "ContentTypeIds",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "DeletedAt",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "DeletedBy",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "HasUniquePermissions",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "Kind",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "MaxVersions",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "SystemKey",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "TemplateKey",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "Versioning",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "ContentTypeId",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "DeletedAt",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "DeletedBy",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "HasUniquePermissions",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "IsFolder",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "ParentId",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "ScopeId",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "WorkspaceId",
            table: "lists");

        migrationBuilder.AddColumn<string>(
            name: "Fields",
            table: "lists",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.CreateIndex(
            name: "IX_lists_TenantId_Name",
            table: "lists",
            columns: new[] { "TenantId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Title",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Title" });
    }
}
