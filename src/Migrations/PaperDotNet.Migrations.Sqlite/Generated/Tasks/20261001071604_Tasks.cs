using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Tasks;

/// <inheritdoc />
public partial class _20261001071604_Tasks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "task_checklists",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Done = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_task_checklists", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "task_links",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetWorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetListId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_task_links", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "task_recurrences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Rule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_task_recurrences", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_task_checklists_TenantId_ItemId_Position",
            table: "task_checklists",
            columns: new[] { "TenantId", "ItemId", "Position" });

        migrationBuilder.CreateIndex(
            name: "IX_task_links_TenantId_ItemId_TargetItemId_Kind",
            table: "task_links",
            columns: new[] { "TenantId", "ItemId", "TargetItemId", "Kind" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_task_links_TenantId_TargetItemId_Kind",
            table: "task_links",
            columns: new[] { "TenantId", "TargetItemId", "Kind" });

        migrationBuilder.CreateIndex(
            name: "IX_task_recurrences_TenantId_ItemId",
            table: "task_recurrences",
            columns: new[] { "TenantId", "ItemId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "task_checklists");

        migrationBuilder.DropTable(
            name: "task_links");

        migrationBuilder.DropTable(
            name: "task_recurrences");
    }
}
