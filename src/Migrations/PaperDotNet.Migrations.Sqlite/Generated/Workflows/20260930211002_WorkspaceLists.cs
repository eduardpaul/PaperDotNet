using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows;

/// <inheritdoc />
public partial class _20260930211002_WorkspaceLists : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Workflows of pre-release builds belonged to the tenant, not to a workspace, like their lists: they go.
        migrationBuilder.Sql("DELETE FROM \"workflow_runs\"; DELETE FROM \"workflow_versions\"; DELETE FROM \"workflows\";");

        migrationBuilder.DropIndex(
            name: "IX_workflows_TenantId_Enabled",
            table: "workflows");

        migrationBuilder.DropIndex(
            name: "IX_workflows_TenantId_Name",
            table: "workflows");

        migrationBuilder.AddColumn<Guid>(
            name: "WorkspaceId",
            table: "workflows",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "WorkspaceId",
            table: "workflow_runs",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.CreateIndex(
            name: "IX_workflows_TenantId_WorkspaceId_Enabled",
            table: "workflows",
            columns: new[] { "TenantId", "WorkspaceId", "Enabled" });

        migrationBuilder.CreateIndex(
            name: "IX_workflows_TenantId_WorkspaceId_Name",
            table: "workflows",
            columns: new[] { "TenantId", "WorkspaceId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_workflow_runs_TenantId_WorkspaceId_Id",
            table: "workflow_runs",
            columns: new[] { "TenantId", "WorkspaceId", "Id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_workflows_TenantId_WorkspaceId_Enabled",
            table: "workflows");

        migrationBuilder.DropIndex(
            name: "IX_workflows_TenantId_WorkspaceId_Name",
            table: "workflows");

        migrationBuilder.DropIndex(
            name: "IX_workflow_runs_TenantId_WorkspaceId_Id",
            table: "workflow_runs");

        migrationBuilder.DropColumn(
            name: "WorkspaceId",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "WorkspaceId",
            table: "workflow_runs");

        migrationBuilder.CreateIndex(
            name: "IX_workflows_TenantId_Enabled",
            table: "workflows",
            columns: new[] { "TenantId", "Enabled" });

        migrationBuilder.CreateIndex(
            name: "IX_workflows_TenantId_Name",
            table: "workflows",
            columns: new[] { "TenantId", "Name" },
            unique: true);
    }
}
