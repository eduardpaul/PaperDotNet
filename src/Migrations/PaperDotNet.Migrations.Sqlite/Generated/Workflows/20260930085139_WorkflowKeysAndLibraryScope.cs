using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowKeysAndLibraryScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_built_in_key",
                table: "automation_definitions");

            migrationBuilder.AddColumn<string>(
                name: "key",
                table: "automation_definitions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "list_id",
                table: "automation_definitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_built_in_key_list_id",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "built_in_key", "list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_key",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_built_in_key_list_id",
                table: "automation_definitions");

            migrationBuilder.DropIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_key",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "key",
                table: "automation_definitions");

            migrationBuilder.DropColumn(
                name: "list_id",
                table: "automation_definitions");

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_built_in_key",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "built_in_key" },
                unique: true);
        }
    }
}
