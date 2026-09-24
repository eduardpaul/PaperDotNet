using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class PermissionsAndHome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_unique_permissions",
                schema: "lists",
                table: "lists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "system_key",
                schema: "lists",
                table: "lists",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_unique_permissions",
                schema: "lists",
                table: "items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "scope_id",
                schema: "lists",
                table: "items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "permission_grants",
                schema: "lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_grants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_workspace_id_system_key",
                schema: "lists",
                table: "lists",
                columns: new[] { "workspace_id", "system_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_list_id_scope_id",
                schema: "lists",
                table: "items",
                columns: new[] { "list_id", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "ix_permission_grants_list_id",
                schema: "lists",
                table: "permission_grants",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_permission_grants_object_id_principal_type_principal_id",
                schema: "lists",
                table: "permission_grants",
                columns: new[] { "object_id", "principal_type", "principal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_permission_grants_tenant_id",
                schema: "lists",
                table: "permission_grants",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission_grants",
                schema: "lists");

            migrationBuilder.DropIndex(
                name: "ix_lists_workspace_id_system_key",
                schema: "lists",
                table: "lists");

            migrationBuilder.DropIndex(
                name: "ix_items_list_id_scope_id",
                schema: "lists",
                table: "items");

            migrationBuilder.DropColumn(
                name: "has_unique_permissions",
                schema: "lists",
                table: "lists");

            migrationBuilder.DropColumn(
                name: "system_key",
                schema: "lists",
                table: "lists");

            migrationBuilder.DropColumn(
                name: "has_unique_permissions",
                schema: "lists",
                table: "items");

            migrationBuilder.DropColumn(
                name: "scope_id",
                schema: "lists",
                table: "items");
        }
    }
}
