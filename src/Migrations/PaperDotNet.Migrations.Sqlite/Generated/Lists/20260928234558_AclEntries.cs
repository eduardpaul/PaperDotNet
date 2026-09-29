using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class AclEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_permission_grants");

            // Items that inherited from their list now name the list as their scope (ADR-0035).
            migrationBuilder.Sql("UPDATE lists_items SET scope_id = list_id WHERE scope_id IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "scope_id",
                table: "lists_items",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "lists_acl_entries",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    level = table.Column<int>(type: "INTEGER", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_acl_entries", x => new { x.scope_id, x.principal_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_acl_entries_list_id",
                table: "lists_acl_entries",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_acl_entries_principal_id_list_id_scope_id_level_tenant_id",
                table: "lists_acl_entries",
                columns: new[] { "principal_id", "list_id", "scope_id", "level", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_acl_entries_tenant_id",
                table: "lists_acl_entries",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_acl_entries");

            migrationBuilder.AlterColumn<Guid>(
                name: "scope_id",
                table: "lists_items",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.CreateTable(
                name: "lists_permission_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    object_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_type = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_permission_grants", x => x.id);
                });

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
    }
}
