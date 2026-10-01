using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows;

/// <inheritdoc />
public partial class _20261001212430_WorkflowRunsByItem : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_workflow_runs_TenantId_ItemId_Id",
            table: "workflow_runs",
            columns: new[] { "TenantId", "ItemId", "Id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_workflow_runs_TenantId_ItemId_Id",
            table: "workflow_runs");
    }
}
