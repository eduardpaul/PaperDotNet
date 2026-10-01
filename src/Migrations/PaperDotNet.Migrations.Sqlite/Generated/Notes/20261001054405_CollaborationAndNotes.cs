using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Notes;

/// <inheritdoc />
public partial class _20261001054405_CollaborationAndNotes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "note_links",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                Target = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                NormalizedTarget = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                Heading = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                Alias = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                Embed = table.Column<bool>(type: "INTEGER", nullable: false),
                TargetItemId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_note_links", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "notes",
            columns: table => new
            {
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notes", x => x.ItemId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_note_links_TenantId_SourceItemId",
            table: "note_links",
            columns: new[] { "TenantId", "SourceItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_note_links_TenantId_TargetItemId",
            table: "note_links",
            columns: new[] { "TenantId", "TargetItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_note_links_TenantId_WorkspaceId_NormalizedTarget",
            table: "note_links",
            columns: new[] { "TenantId", "WorkspaceId", "NormalizedTarget" });

        migrationBuilder.CreateIndex(
            name: "IX_notes_TenantId_WorkspaceId_NormalizedTitle",
            table: "notes",
            columns: new[] { "TenantId", "WorkspaceId", "NormalizedTitle" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "note_links");

        migrationBuilder.DropTable(
            name: "notes");
    }
}
