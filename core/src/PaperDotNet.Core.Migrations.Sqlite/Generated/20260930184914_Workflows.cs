using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Core.Migrations.Sqlite.Generated;

/// <inheritdoc />
public partial class _20260930184914_Workflows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workflows",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CurrentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                TriggerTypes = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflows", x => x.Id);
                table.ForeignKey(
                    name: "FK_workflows_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "workflow_runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkflowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Node = table.Column<string>(type: "TEXT", nullable: true),
                StepExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                NodesRun = table.Column<int>(type: "INTEGER", nullable: false),
                Trigger = table.Column<string>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                Data = table.Column<string>(type: "TEXT", nullable: true),
                Outputs = table.Column<string>(type: "TEXT", nullable: false),
                Variables = table.Column<string>(type: "TEXT", nullable: false),
                Log = table.Column<string>(type: "TEXT", nullable: false),
                Error = table.Column<string>(type: "TEXT", nullable: true),
                FailedNode = table.Column<string>(type: "TEXT", nullable: true),
                StartedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Depth = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_runs", x => x.Id);
                table.ForeignKey(
                    name: "FK_workflow_runs_workflows_WorkflowId",
                    column: x => x.WorkflowId,
                    principalTable: "workflows",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "workflow_versions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                Number = table.Column<int>(type: "INTEGER", nullable: false),
                Definition = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_versions", x => x.Id);
                table.ForeignKey(
                    name: "FK_workflow_versions_workflows_WorkflowId",
                    column: x => x.WorkflowId,
                    principalTable: "workflows",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_runs_TenantId_WorkflowId_Id",
            table: "workflow_runs",
            columns: new[] { "TenantId", "WorkflowId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_runs_WorkflowId",
            table: "workflow_runs",
            column: "WorkflowId");

        migrationBuilder.CreateIndex(
            name: "IX_workflow_versions_TenantId_WorkflowId_Number",
            table: "workflow_versions",
            columns: new[] { "TenantId", "WorkflowId", "Number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_workflow_versions_WorkflowId",
            table: "workflow_versions",
            column: "WorkflowId");

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

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workflow_runs");

        migrationBuilder.DropTable(
            name: "workflow_versions");

        migrationBuilder.DropTable(
            name: "workflows");
    }
}
