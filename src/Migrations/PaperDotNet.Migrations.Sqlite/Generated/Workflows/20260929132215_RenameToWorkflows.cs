using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class RenameToWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "automation_id",
                table: "automation_versions",
                newName: "workflow_id");

            migrationBuilder.RenameIndex(
                name: "ix_automation_versions_automation_id_number",
                table: "automation_versions",
                newName: "ix_automation_versions_workflow_id_number");

            migrationBuilder.RenameColumn(
                name: "automation_version",
                table: "automation_runs",
                newName: "workflow_version");

            migrationBuilder.RenameColumn(
                name: "automation_id",
                table: "automation_runs",
                newName: "workflow_id");

            migrationBuilder.RenameIndex(
                name: "ix_automation_runs_tenant_id_automation_id_started_at",
                table: "automation_runs",
                newName: "ix_automation_runs_tenant_id_workflow_id_started_at");

            migrationBuilder.RenameIndex(
                name: "ix_automation_runs_automation_id_event_id",
                table: "automation_runs",
                newName: "ix_automation_runs_workflow_id_event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "workflow_id",
                table: "automation_versions",
                newName: "automation_id");

            migrationBuilder.RenameIndex(
                name: "ix_automation_versions_workflow_id_number",
                table: "automation_versions",
                newName: "ix_automation_versions_automation_id_number");

            migrationBuilder.RenameColumn(
                name: "workflow_version",
                table: "automation_runs",
                newName: "automation_version");

            migrationBuilder.RenameColumn(
                name: "workflow_id",
                table: "automation_runs",
                newName: "automation_id");

            migrationBuilder.RenameIndex(
                name: "ix_automation_runs_workflow_id_event_id",
                table: "automation_runs",
                newName: "ix_automation_runs_automation_id_event_id");

            migrationBuilder.RenameIndex(
                name: "ix_automation_runs_tenant_id_workflow_id_started_at",
                table: "automation_runs",
                newName: "ix_automation_runs_tenant_id_automation_id_started_at");
        }
    }
}
