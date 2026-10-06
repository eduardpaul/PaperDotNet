using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class SystemWorkflowRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "system",
                table: "automation_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "automation_definitions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_role",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "role" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_role",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "system",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "role",
                table: "automation_definitions");
        }
    }
}
