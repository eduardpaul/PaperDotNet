using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowBuiltIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "built_in_key",
                schema: "automation",
                table: "definitions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "copied_from",
                schema: "automation",
                table: "definitions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parameters",
                schema: "automation",
                table: "definitions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_built_in_key",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "built_in_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_definitions_tenant_id_workspace_id_built_in_key",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropColumn(
                name: "built_in_key",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropColumn(
                name: "copied_from",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropColumn(
                name: "parameters",
                schema: "automation",
                table: "definitions");
        }
    }
}
