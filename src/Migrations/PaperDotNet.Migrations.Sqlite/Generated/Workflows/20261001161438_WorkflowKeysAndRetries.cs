using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows;

/// <inheritdoc />
public partial class _20261001161438_WorkflowKeysAndRetries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Key",
            table: "workflows",
            type: "TEXT",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "CompletedAtUnixMs",
            table: "workflow_runs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "NodeAttempts",
            table: "workflow_runs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "IX_workflows_TenantId_WorkspaceId_Key",
            table: "workflows",
            columns: new[] { "TenantId", "WorkspaceId", "Key" });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_runs_TenantId_Status_CompletedAtUnixMs",
            table: "workflow_runs",
            columns: new[] { "TenantId", "Status", "CompletedAtUnixMs" });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_runs_TenantId_WorkflowId_ItemId_Status",
            table: "workflow_runs",
            columns: new[] { "TenantId", "WorkflowId", "ItemId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_workflows_TenantId_WorkspaceId_Key",
            table: "workflows");

        migrationBuilder.DropIndex(
            name: "IX_workflow_runs_TenantId_Status_CompletedAtUnixMs",
            table: "workflow_runs");

        migrationBuilder.DropIndex(
            name: "IX_workflow_runs_TenantId_WorkflowId_ItemId_Status",
            table: "workflow_runs");

        migrationBuilder.DropColumn(
            name: "Key",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "CompletedAtUnixMs",
            table: "workflow_runs");

        migrationBuilder.DropColumn(
            name: "NodeAttempts",
            table: "workflow_runs");
    }
}
