using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists;

/// <inheritdoc />
public partial class _20261001220845_HomeLibraries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_lists_TenantId_WorkspaceId_SystemKey",
            table: "lists",
            columns: new[] { "TenantId", "WorkspaceId", "SystemKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_lists_TenantId_WorkspaceId_SystemKey",
            table: "lists");
    }
}
