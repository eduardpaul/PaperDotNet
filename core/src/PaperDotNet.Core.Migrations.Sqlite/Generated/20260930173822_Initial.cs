using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Core.Migrations.Sqlite.Generated;

/// <inheritdoc />
public partial class _20260930173822_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_entries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                Summary = table.Column<string>(type: "TEXT", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_entries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "tenants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Identifier = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "lists",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                Fields = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_lists", x => x.Id);
                table.ForeignKey(
                    name: "FK_lists_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.Id);
                table.ForeignKey(
                    name: "FK_users_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "list_items",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Fields = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_list_items", x => x.Id);
                table.ForeignKey(
                    name: "FK_list_items_lists_ListId",
                    column: x => x.ListId,
                    principalTable: "lists",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_entries_TenantId_Id",
            table: "audit_entries",
            columns: new[] { "TenantId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_entries_TenantId_TargetId",
            table: "audit_entries",
            columns: new[] { "TenantId", "TargetId" });

        migrationBuilder.CreateIndex(
            name: "IX_list_items_ListId",
            table: "list_items",
            column: "ListId");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Title",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Title" });

        migrationBuilder.CreateIndex(
            name: "IX_lists_TenantId_Name",
            table: "lists",
            columns: new[] { "TenantId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tenants_Identifier",
            table: "tenants",
            column: "Identifier",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_users_TenantId_NormalizedUserName",
            table: "users",
            columns: new[] { "TenantId", "NormalizedUserName" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_entries");

        migrationBuilder.DropTable(
            name: "list_items");

        migrationBuilder.DropTable(
            name: "users");

        migrationBuilder.DropTable(
            name: "lists");

        migrationBuilder.DropTable(
            name: "tenants");
    }
}
