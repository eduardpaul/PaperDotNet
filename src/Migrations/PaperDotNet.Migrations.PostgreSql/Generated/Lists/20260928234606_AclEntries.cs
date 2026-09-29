using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class AclEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission_grants",
                schema: "lists");

            // Items that inherited from their list now name the list as their scope (ADR-0035).
            migrationBuilder.Sql("UPDATE lists.items SET scope_id = list_id WHERE scope_id IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "scope_id",
                schema: "lists",
                table: "items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "acl_entries",
                schema: "lists",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acl_entries", x => new { x.scope_id, x.principal_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_acl_entries_list_id",
                schema: "lists",
                table: "acl_entries",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_acl_entries_principal_id_list_id_scope_id_level_tenant_id",
                schema: "lists",
                table: "acl_entries",
                columns: new[] { "principal_id", "list_id", "scope_id", "level", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_acl_entries_tenant_id",
                schema: "lists",
                table: "acl_entries",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acl_entries",
                schema: "lists");

            migrationBuilder.AlterColumn<Guid>(
                name: "scope_id",
                schema: "lists",
                table: "items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "permission_grants",
                schema: "lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_grants", x => x.id);
                });

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
    }
}
