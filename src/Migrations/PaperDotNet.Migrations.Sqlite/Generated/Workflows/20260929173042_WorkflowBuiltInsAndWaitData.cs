using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowBuiltInsAndWaitData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "built_in_key",
                table: "automation_definitions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "copied_from",
                table: "automation_definitions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parameters",
                table: "automation_definitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "data",
                table: "automation_bookmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "run_again",
                table: "automation_bookmarks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_built_in_key",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "built_in_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_built_in_key",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "built_in_key",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "copied_from",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "parameters",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "data",
                table: "automation_bookmarks");

            migrationBuilder.DropColumn(
                name: "run_again",
                table: "automation_bookmarks");
        }
    }
}
