using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Collaboration;

/// <inheritdoc />
public partial class _20261001054358_CollaborationAndNotes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "comments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                Mentions = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_comments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "item_activity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ActorId = table.Column<Guid>(type: "TEXT", nullable: true),
                Summary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                ChangedFields = table.Column<string>(type: "TEXT", nullable: false),
                DeduplicationKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                AtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_item_activity", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_comments_TenantId_ItemId",
            table: "comments",
            columns: new[] { "TenantId", "ItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_comments_TenantId_ParentId",
            table: "comments",
            columns: new[] { "TenantId", "ParentId" });

        migrationBuilder.CreateIndex(
            name: "IX_item_activity_TenantId_DeduplicationKey",
            table: "item_activity",
            columns: new[] { "TenantId", "DeduplicationKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_item_activity_TenantId_ItemId_AtUnixMs",
            table: "item_activity",
            columns: new[] { "TenantId", "ItemId", "AtUnixMs" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "comments");

        migrationBuilder.DropTable(
            name: "item_activity");
    }
}
