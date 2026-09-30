using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity;

/// <inheritdoc />
public partial class _20260930201333_GroupsRolesTokens : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Administrators become role assignments (BuiltInRoles.EnsureAsync on start), not a renamed column.
        migrationBuilder.DropColumn(
            name: "IsAdmin",
            table: "users");

        migrationBuilder.AddColumn<int>(
            name: "AccessFailedCount",
            table: "users",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Email",
            table: "users",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LockoutEnd",
            table: "users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "api_tokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                Scopes = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_tokens", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "group_closure",
            columns: table => new
            {
                GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                AncestorId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_group_closure", x => new { x.GroupId, x.AncestorId });
            });

        migrationBuilder.CreateTable(
            name: "group_members",
            columns: table => new
            {
                GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_group_members", x => new { x.GroupId, x.UserId });
            });

        migrationBuilder.CreateTable(
            name: "group_nestings",
            columns: table => new
            {
                GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                MemberGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_group_nestings", x => new { x.GroupId, x.MemberGroupId });
            });

        migrationBuilder.CreateTable(
            name: "groups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_groups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "preferences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Language = table.Column<string>(type: "TEXT", maxLength: 35, nullable: true),
                TimeZone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                DateFormat = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                TimeFormat = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                NumberFormat = table.Column<string>(type: "TEXT", maxLength: 35, nullable: true),
                Theme = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                DocumentLanguages = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_preferences", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "role_assignments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                PrincipalType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_role_assignments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "roles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                GrantsAllScopes = table.Column<bool>(type: "INTEGER", nullable: false),
                Scopes = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_roles", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_api_tokens_Hash",
            table: "api_tokens",
            column: "Hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_tokens_TenantId_UserId",
            table: "api_tokens",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_group_closure_TenantId_AncestorId_GroupId",
            table: "group_closure",
            columns: new[] { "TenantId", "AncestorId", "GroupId" });

        migrationBuilder.CreateIndex(
            name: "IX_group_members_TenantId_UserId",
            table: "group_members",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_group_nestings_TenantId_MemberGroupId",
            table: "group_nestings",
            columns: new[] { "TenantId", "MemberGroupId" });

        migrationBuilder.CreateIndex(
            name: "IX_groups_TenantId_Name",
            table: "groups",
            columns: new[] { "TenantId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_preferences_TenantId_UserId",
            table: "preferences",
            columns: new[] { "TenantId", "UserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_role_assignments_RoleId_PrincipalId_PrincipalType",
            table: "role_assignments",
            columns: new[] { "RoleId", "PrincipalId", "PrincipalType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_role_assignments_TenantId_PrincipalId",
            table: "role_assignments",
            columns: new[] { "TenantId", "PrincipalId" });

        migrationBuilder.CreateIndex(
            name: "IX_roles_TenantId_Name",
            table: "roles",
            columns: new[] { "TenantId", "Name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "api_tokens");

        migrationBuilder.DropTable(
            name: "group_closure");

        migrationBuilder.DropTable(
            name: "group_members");

        migrationBuilder.DropTable(
            name: "group_nestings");

        migrationBuilder.DropTable(
            name: "groups");

        migrationBuilder.DropTable(
            name: "preferences");

        migrationBuilder.DropTable(
            name: "role_assignments");

        migrationBuilder.DropTable(
            name: "roles");

        migrationBuilder.DropColumn(
            name: "DeletedAt",
            table: "users");

        migrationBuilder.DropColumn(
            name: "Email",
            table: "users");

        migrationBuilder.DropColumn(
            name: "LockoutEnd",
            table: "users");

        migrationBuilder.DropColumn(
            name: "AccessFailedCount",
            table: "users");

        migrationBuilder.AddColumn<bool>(
            name: "IsAdmin",
            table: "users",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }
}
