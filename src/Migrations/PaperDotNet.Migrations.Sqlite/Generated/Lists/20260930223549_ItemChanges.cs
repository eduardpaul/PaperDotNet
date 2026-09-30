using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists;

/// <inheritdoc />
public partial class _20260930223549_ItemChanges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "item_changes",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                ScopeId = table.Column<Guid>(type: "TEXT", nullable: true),
                FromScopeId = table.Column<Guid>(type: "TEXT", nullable: true),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                At = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_item_changes", x => x.Sequence);
            });

        migrationBuilder.CreateIndex(
            name: "IX_item_changes_TenantId_At",
            table: "item_changes",
            columns: new[] { "TenantId", "At" });

        migrationBuilder.CreateIndex(
            name: "IX_item_changes_TenantId_ListId_Sequence",
            table: "item_changes",
            columns: new[] { "TenantId", "ListId", "Sequence" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "item_changes");
    }
}
