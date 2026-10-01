using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists;

/// <inheritdoc />
public partial class _20261001103823_SmartFolders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "smart_folders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                OwnerId = table.Column<Guid>(type: "TEXT", nullable: true),
                Definition = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_smart_folders", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_smart_folders_TenantId_OwnerId",
            table: "smart_folders",
            columns: new[] { "TenantId", "OwnerId" });

        migrationBuilder.CreateIndex(
            name: "IX_smart_folders_TenantId_WorkspaceId",
            table: "smart_folders",
            columns: new[] { "TenantId", "WorkspaceId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "smart_folders");
    }
}
