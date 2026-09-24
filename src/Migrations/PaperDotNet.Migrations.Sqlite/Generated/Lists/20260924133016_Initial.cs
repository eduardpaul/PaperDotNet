using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lists_content_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_built_in = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    fields = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_content_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lists_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    content_type_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    is_folder = table.Column<bool>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    fields = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lists_lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    allow_folders = table.Column<bool>(type: "INTEGER", nullable: false),
                    content_type_ids = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_lists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lists_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    columns = table.Column<string>(type: "TEXT", nullable: false),
                    filter = table.Column<string>(type: "TEXT", nullable: true),
                    order_by = table.Column<string>(type: "TEXT", nullable: true),
                    group_by = table.Column<string>(type: "TEXT", nullable: true),
                    layout = table.Column<int>(type: "INTEGER", nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_views", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_content_types_tenant_id",
                table: "lists_content_types",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_content_types_tenant_id_name",
                table: "lists_content_types",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_parent_id",
                table: "lists_items",
                columns: new[] { "list_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_tenant_id",
                table: "lists_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_lists_tenant_id",
                table: "lists_lists",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_lists_tenant_id_workspace_id",
                table: "lists_lists",
                columns: new[] { "tenant_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_views_list_id",
                table: "lists_views",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_views_tenant_id",
                table: "lists_views",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_content_types");

            migrationBuilder.DropTable(
                name: "lists_items");

            migrationBuilder.DropTable(
                name: "lists_lists");

            migrationBuilder.DropTable(
                name: "lists_views");
        }
    }
}
