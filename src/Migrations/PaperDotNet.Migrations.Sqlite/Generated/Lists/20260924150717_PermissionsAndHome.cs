using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class PermissionsAndHome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_unique_permissions",
                table: "lists_lists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "system_key",
                table: "lists_lists",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_unique_permissions",
                table: "lists_items",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "scope_id",
                table: "lists_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lists_permission_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    object_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_type = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_permission_grants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_lists_workspace_id_system_key",
                table: "lists_lists",
                columns: new[] { "workspace_id", "system_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_scope_id",
                table: "lists_items",
                columns: new[] { "list_id", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_permission_grants_list_id",
                table: "lists_permission_grants",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_permission_grants_object_id_principal_type_principal_id",
                table: "lists_permission_grants",
                columns: new[] { "object_id", "principal_type", "principal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_permission_grants_tenant_id",
                table: "lists_permission_grants",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_permission_grants");

            migrationBuilder.DropIndex(
                name: "ix_lists_lists_workspace_id_system_key",
                table: "lists_lists");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_scope_id",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "has_unique_permissions",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "system_key",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "has_unique_permissions",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "scope_id",
                table: "lists_items");
        }
    }
}
