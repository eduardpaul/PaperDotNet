using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowKeysAndLibraryScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_definitions_tenant_id_workspace_id_built_in_key",
                schema: "automation",
                table: "definitions");

            migrationBuilder.AddColumn<string>(
                name: "key",
                schema: "automation",
                table: "definitions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "list_id",
                schema: "automation",
                table: "definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_built_in_key_list_id",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "built_in_key", "list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_key",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_definitions_tenant_id_workspace_id_built_in_key_list_id",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropIndex(
                name: "ix_definitions_tenant_id_workspace_id_key",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropColumn(
                name: "key",
                schema: "automation",
                table: "definitions");

            migrationBuilder.DropColumn(
                name: "list_id",
                schema: "automation",
                table: "definitions");

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_built_in_key",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "built_in_key" },
                unique: true);
        }
    }
}
