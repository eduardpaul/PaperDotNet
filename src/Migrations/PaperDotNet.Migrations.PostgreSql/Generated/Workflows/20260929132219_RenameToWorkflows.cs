using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class RenameToWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "automation_id",
                schema: "automation",
                table: "versions",
                newName: "workflow_id");

            migrationBuilder.RenameIndex(
                name: "ix_versions_automation_id_number",
                schema: "automation",
                table: "versions",
                newName: "ix_versions_workflow_id_number");

            migrationBuilder.RenameColumn(
                name: "automation_version",
                schema: "automation",
                table: "runs",
                newName: "workflow_version");

            migrationBuilder.RenameColumn(
                name: "automation_id",
                schema: "automation",
                table: "runs",
                newName: "workflow_id");

            migrationBuilder.RenameIndex(
                name: "ix_runs_tenant_id_automation_id_started_at",
                schema: "automation",
                table: "runs",
                newName: "ix_runs_tenant_id_workflow_id_started_at");

            migrationBuilder.RenameIndex(
                name: "ix_runs_automation_id_event_id",
                schema: "automation",
                table: "runs",
                newName: "ix_runs_workflow_id_event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "workflow_id",
                schema: "automation",
                table: "versions",
                newName: "automation_id");

            migrationBuilder.RenameIndex(
                name: "ix_versions_workflow_id_number",
                schema: "automation",
                table: "versions",
                newName: "ix_versions_automation_id_number");

            migrationBuilder.RenameColumn(
                name: "workflow_version",
                schema: "automation",
                table: "runs",
                newName: "automation_version");

            migrationBuilder.RenameColumn(
                name: "workflow_id",
                schema: "automation",
                table: "runs",
                newName: "automation_id");

            migrationBuilder.RenameIndex(
                name: "ix_runs_workflow_id_event_id",
                schema: "automation",
                table: "runs",
                newName: "ix_runs_automation_id_event_id");

            migrationBuilder.RenameIndex(
                name: "ix_runs_tenant_id_workflow_id_started_at",
                schema: "automation",
                table: "runs",
                newName: "ix_runs_tenant_id_automation_id_started_at");
        }
    }
}
