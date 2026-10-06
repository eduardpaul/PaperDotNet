using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class SystemWorkflowRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "system",
                schema: "automation",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "role",
                schema: "automation",
                table: "definitions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_role",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "role" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_definitions_tenant_id_workspace_id_role",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropColumn(
                name: "system",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "automation",
                table: "definitions");
        }
    }
}
